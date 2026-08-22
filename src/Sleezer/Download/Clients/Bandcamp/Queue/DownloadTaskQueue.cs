using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NLog;

namespace NzbDrone.Core.Download.Clients.Bandcamp
{
    /// <summary>
    /// Background download processor using System.Threading.Channels for async
    /// producer/consumer semantics. Downloads are enqueued via EnqueueAsync() and
    /// processed sequentially with rate limiting. The queue runs until disposed.
    /// </summary>
    public class DownloadTaskQueue : IBandcampDownloadQueue
    {
        private readonly Channel<BandcampDownloadItem> _channel;
        private readonly CancellationTokenSource _cts = new();

        // Per-item sources so RemoveItem can stop one download without stopping the
        // queue — the shape the other three clients already use.
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _itemCancellations = new();
        private readonly IBandcampDownloadProxy _downloadProxy;
        private readonly IBandcampDownloadRegistry _registry;
        private readonly Logger _logger;
        private readonly Task _consumerTask;
        private volatile bool _disposed;

        public DownloadTaskQueue(
            IBandcampDownloadProxy downloadProxy,
            IBandcampDownloadRegistry registry,
            Logger logger)
        {
            _downloadProxy = downloadProxy;
            _registry = registry;
            _logger = logger;

            // Bounded channel to limit memory pressure; writers block when full
            _channel = Channel.CreateBounded<BandcampDownloadItem>(new BoundedChannelOptions(100)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true
            });

            // Start the background consumer loop
            _consumerTask = Task.Run(() => ProcessQueueAsync(_cts.Token));
        }

        /// <summary>
        /// Enqueues a download item for background processing.
        /// Returns the download ID immediately while processing continues.
        /// </summary>
        /// <param name="item">The download to process (must have Cookies, AlbumUrl, and OutputPath set).</param>
        /// <returns>The download ID for tracking.</returns>
        public async Task<string> EnqueueAsync(BandcampDownloadItem item)
        {
            // Registered before publication so RemoveItem can cancel an item that has
            // not been picked up yet.
            _itemCancellations[item.DownloadId] = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);

            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(DownloadTaskQueue));
            }

            item.Status = BandcampDownloadStatus.Queued;
            item.QueuedAt = DateTime.UtcNow;
            item.Phase = "queued";

            _registry.Upsert(item);

            await _channel.Writer.WriteAsync(item, _cts.Token).ConfigureAwait(false);

            _logger.Debug("Bandcamp download queue: Enqueued download {0} for '{1}'",
                item.DownloadId, item.AlbumUrl);

            return item.DownloadId;
        }

        /// <summary>
        /// Returns all tracked download items (queued, active, completed, failed).
        /// Used by the download client's GetItems() to report state to Lidarr.
        /// </summary>
        public ConcurrentDictionary<string, BandcampDownloadItem> GetItems()
        {
            return _registry.GetItems();
        }

        /// <summary>
        /// Removes a completed/failed item from tracking.
        /// </summary>
        public void RemoveItem(string downloadId)
        {
            // Cancel before deregistering: dropping the registry entry alone left the
            // download running and still writing files after Lidarr had been told the
            // item was gone.
            if (_itemCancellations.TryGetValue(downloadId, out var itemCts))
            {
                try
                {
                    itemCts.Cancel();
                    _logger.Debug("Bandcamp download queue: Cancelled in-flight download {0}", downloadId);
                }
                catch (ObjectDisposedException)
                {
                    // The item finished between the lookup and the cancel; nothing to stop.
                }
            }

            _registry.Remove(downloadId);
            _logger.Debug("Bandcamp download queue: Removed item {0}", downloadId);
        }

        private async Task ProcessQueueAsync(CancellationToken cancellationToken)
        {
            _logger.Debug("Bandcamp download queue: Background processor started");

            try
            {
                await foreach (var item in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    // The source is created at enqueue time, so an item removed while
                    // still sitting in the channel is already cancelled by the time it
                    // gets here rather than downloading anyway.
                    if (!_itemCancellations.TryGetValue(item.DownloadId, out var itemCts) || itemCts.IsCancellationRequested)
                    {
                        _logger.Debug("Bandcamp download queue: Skipping {0}; removed before it started", item.DownloadId);
                        _itemCancellations.TryRemove(item.DownloadId, out _);
                        itemCts?.Dispose();
                        continue;
                    }

                    try
                    {
                        await ProcessItemAsync(item, itemCts.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        _itemCancellations.TryRemove(item.DownloadId, out _);
                        itemCts.Dispose();
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Graceful shutdown — expected
                _logger.Debug("Bandcamp download queue: Processor shutting down");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Bandcamp download queue: Processor encountered fatal error");
            }
        }

        private async Task ProcessItemAsync(BandcampDownloadItem item, CancellationToken cancellationToken)
        {
            _logger.Debug("Bandcamp download queue: Starting download {0} for '{1}'",
                item.DownloadId, item.AlbumUrl);

            try
            {
                item.Status = BandcampDownloadStatus.Resolving;
                item.Phase = "resolving";

                await _downloadProxy.ExecuteDownloadAsync(item, cancellationToken).ConfigureAwait(false);

                item.Status = BandcampDownloadStatus.Completed;
                item.Progress = 1.0;
                item.CompletedAt = DateTime.UtcNow;
                item.Phase = "completed";

                _logger.Debug("Bandcamp download queue: Download {0} completed successfully -> {1}",
                    item.DownloadId, item.OutputPath);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                item.Status = BandcampDownloadStatus.Failed;
                item.ErrorMessage = "Download was cancelled";
                item.CompletedAt = DateTime.UtcNow;
                item.Phase = "cancelled";

                _logger.Debug("Bandcamp download queue: Download {0} was cancelled", item.DownloadId);
            }
            catch (Exception ex)
            {
                // Capture before overwriting, or the message reports the phase "failed"
                // as the phase that failed.
                var failedPhase = item.Phase;

                item.Status = BandcampDownloadStatus.Failed;
                item.ErrorMessage = ex.Message;
                item.CompletedAt = DateTime.UtcNow;
                item.Phase = "failed";

                _logger.Warn(ex, "Bandcamp download queue: Download {0} failed during phase '{1}'",
                    item.DownloadId, failedPhase);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cts.Cancel();
            _channel.Writer.TryComplete();

            var stopped = false;
            try
            {
                stopped = _consumerTask.Wait(TimeSpan.FromSeconds(10));
            }
            catch (AggregateException)
            {
                // Consumer threw on cancellation, which still means it has stopped.
                stopped = true;
            }

            if (stopped)
            {
                _cts.Dispose();
            }
            else
            {
                // Still running — a large extraction can outlast the wait. Disposing now
                // would throw ObjectDisposedException inside the background task the
                // moment it touched the token. Leaking one CTS beats that.
                _logger.Warn("Bandcamp download queue: consumer still running after 10s; leaving its cancellation source alive");
            }
        }
    }
}
