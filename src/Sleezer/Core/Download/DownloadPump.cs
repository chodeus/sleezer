using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Core.Download;

namespace NzbDrone.Plugin.Sleezer.Core.Download
{
    /// Bounded work queue behind the Deezer, Tidal and Qobuz download clients: owns the
    /// channel, the concurrency limit, per-item cancellation and the worker loop. Clients
    /// supply only what happens to one item, via the delegate passed to the constructor.
    public sealed class DownloadPump<TItem>
        where TItem : class, IQueuedDownload
    {
        private readonly Channel<TItem> _queue;
        private readonly List<TItem> _items = new();
        private readonly Dictionary<TItem, CancellationTokenSource> _cancellationSources = new();
        private readonly List<Task> _runningTasks = new();
        private readonly object _lock = new();

        private readonly int _concurrency;
        private readonly string _client;
        private readonly Logger _logger;
        private readonly Func<TItem, CancellationToken, Task> _runItem;

        public DownloadPump(int capacity, int concurrency, string client, Logger logger, Func<TItem, CancellationToken, Task> runItem)
        {
            _queue = Channel.CreateBounded<TItem>(new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.Wait });
            _concurrency = concurrency;
            _client = client;
            _logger = logger;
            _runItem = runItem;
        }

        public void Start()
        {
            // Without the continuation a faulted loop is silent: the queue keeps accepting
            // items and reporting them queued while nothing drains it.
            _ = Task.Run(() => ProcessAsync())
                .ContinueWith(
                    faulted => _logger.Error(faulted.Exception, "{Client} queue handler stopped; no further downloads will be processed until Lidarr restarts.", _client),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
        }

        public async ValueTask EnqueueAsync(TItem item)
        {
            // Registered before the channel write: the consumer can dequeue and ask for the
            // token the instant the write lands, and an unregistered item yields
            // default(CancellationToken), which nothing can ever cancel.
            CancellationTokenSource token = new();
            lock (_lock)
            {
                _items.Add(item);
                _cancellationSources.Add(item, token);
            }

            // Published under the item's own token so an item cancelled while waiting for
            // capacity never reaches a worker at all.
            try
            {
                await _queue.Writer.WriteAsync(item, token.Token);
            }
            catch
            {
                // No worker will ever see this item, so nothing else will clean it up.
                lock (_lock)
                {
                    _items.Remove(item);
                    _cancellationSources.Remove(item);
                }

                token.Dispose();
                throw;
            }
        }

        public void Remove(TItem item)
        {
            if (item == null)
                return;

            lock (_lock)
            {
                // Cancelled but the mapping is kept: the worker may not have read the token
                // yet, and handing it a default one would make the download uncancellable.
                // The worker disposes it when the work actually ends.
                if (_cancellationSources.TryGetValue(item, out var src))
                    src.Cancel();

                _items.Remove(item);
            }
        }

        /// Adds an item that was never enqueued — a completed download recovered from disk.
        /// Returns false when one with the same ID is already tracked.
        public bool TryAddRecovered(TItem item)
        {
            lock (_lock)
            {
                if (_items.Any(i => i.ID == item.ID))
                    return false;

                _items.Add(item);
                return true;
            }
        }

        public TItem[] Listing()
        {
            lock (_lock)
                return _items.ToArray();
        }

        public CancellationToken TokenFor(TItem item)
        {
            lock (_lock)
                return _cancellationSources.TryGetValue(item, out var src) ? src.Token : default;
        }

        private async Task ProcessAsync(CancellationToken stoppingToken = default)
        {
            using SemaphoreSlim semaphore = new(_concurrency, _concurrency);

            while (!stoppingToken.IsCancellationRequested)
            {
                await semaphore.WaitAsync(stoppingToken);

                TItem? item = null;
                try
                {
                    item = await _queue.Reader.ReadAsync(stoppingToken);

                    var handler = HandleAsync(item, semaphore);
                    lock (_lock)
                    {
                        // Pruned here, not inside HandleAsync: a handler that finished
                        // before being added would never be removed.
                        _runningTasks.RemoveAll(t => t.IsCompleted);
                        _runningTasks.Add(handler);
                    }
                }
                catch (OperationCanceledException)
                {
                    semaphore.Release();
                    throw;
                }
                catch (Exception ex)
                {
                    // Per-iteration safety net so one bad item cannot kill the loop.
                    // HandleAsync does not own the semaphore for this item, so release here.
                    if (item != null)
                        item.Status = DownloadItemStatus.Failed;
                    _logger.Error(ex, "{Client} queue iteration failed; loop continues", _client);
                    semaphore.Release();
                }
            }

            List<Task> remaining;
            lock (_lock)
                remaining = _runningTasks.ToList();

            await Task.WhenAll(remaining);
        }

        private async Task HandleAsync(TItem item, SemaphoreSlim semaphore)
        {
            try
            {
                item.Status = DownloadItemStatus.Downloading;
                await _runItem(item, TokenFor(item));
            }
            catch (OperationCanceledException)
            {
                _logger.Trace("{Client} download cancelled: {Title}", _client, item.Title);
            }
            catch (Exception ex)
            {
                item.Status = DownloadItemStatus.Failed;
                _logger.Error(ex, "Error while downloading {Client} album {Title}", _client, item.Title);
            }
            finally
            {
                semaphore.Release();

                // The source outlives Remove so the worker always has a real token; this is
                // the only place that knows the work is over.
                lock (_lock)
                {
                    if (_cancellationSources.Remove(item, out var src))
                        src.Dispose();
                }
            }
        }
    }
}
