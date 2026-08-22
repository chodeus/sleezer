using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Music;
using NzbDrone.Plugin.Sleezer.Core.PostProcessing;
using NzbDrone.Plugin.Sleezer.Metadata.FFmpeg;

namespace NzbDrone.Core.Download.Clients.Qobuz.Queue
{
    public class DownloadTaskQueue
    {
        private const int ConcurrentAlbums = 3;

        private readonly Channel<DownloadItem> _queue;
        private readonly List<DownloadItem> _items = [];
        private readonly Dictionary<DownloadItem, CancellationTokenSource> _cancellationSources = [];
        private readonly List<Task> _runningTasks = [];
        private readonly object _lock = new();

        private readonly Logger _logger;
        private readonly PostProcessRunner _postProcess;
        private QobuzSettings? _settings;

        public DownloadTaskQueue(
            int capacity,
            QobuzSettings? settings,
            ICorruptionScanner corruptionScanner,
            ICorruptionFailureHandler corruptionFailureHandler,
            IPreImportTagger preImportTagger,
            IMetadataFactory metadataFactory,
            IDiskProvider diskProvider,
            Logger logger)
        {
            BoundedChannelOptions options = new(capacity) { FullMode = BoundedChannelFullMode.Wait };
            _queue = Channel.CreateBounded<DownloadItem>(options);
            _settings = settings;
            _logger = logger;
            _postProcess = new PostProcessRunner(corruptionScanner, corruptionFailureHandler, preImportTagger, metadataFactory, diskProvider, logger);
        }

        public void SetSettings(QobuzSettings settings) => _settings = settings;

        public void StartQueueHandler() => Task.Run(() => BackgroundProcessing());

        public async ValueTask QueueBackgroundWorkItemAsync(DownloadItem workItem)
        {
            await _queue.Writer.WriteAsync(workItem);
            CancellationTokenSource token = new();
            lock (_lock)
            {
                _items.Add(workItem);
                _cancellationSources.Add(workItem, token);
            }
        }

        public void RemoveItem(DownloadItem workItem)
        {
            if (workItem == null)
                return;

            lock (_lock)
            {
                if (_cancellationSources.TryGetValue(workItem, out var src))
                {
                    src.Cancel();
                    src.Dispose();
                }

                _items.Remove(workItem);
                _cancellationSources.Remove(workItem);
            }
        }

        public DownloadItem[] GetQueueListing()
        {
            lock (_lock)
                return [.. _items];
        }

        public CancellationToken GetTokenForItem(DownloadItem item)
        {
            lock (_lock)
                return _cancellationSources.TryGetValue(item, out var src) ? src.Token : default;
        }

        private async Task BackgroundProcessing(CancellationToken stoppingToken = default)
        {
            using SemaphoreSlim semaphore = new(ConcurrentAlbums, ConcurrentAlbums);

            while (!stoppingToken.IsCancellationRequested)
            {
                await semaphore.WaitAsync(stoppingToken);

                DownloadItem? item = null;
                try
                {
                    item = await _queue.Reader.ReadAsync(stoppingToken);

                    if (_settings == null)
                    {
                        // The proxy always calls SetSettings before queueing, so this is
                        // genuinely unexpected — drop the item rather than kill the loop.
                        _logger.Error("Qobuz queue received item before settings populated; marking failed: {Title}", item.Title);
                        item.Status = DownloadItemStatus.Failed;
                        semaphore.Release();
                        continue;
                    }

                    var token = GetTokenForItem(item);
                    var downloadTask = item.DoDownload(_settings, _logger, token);
                    var handler = HandleTask(item, downloadTask, semaphore);

                    lock (_lock)
                    {
                        // Prune here rather than from inside HandleTask: a handler that
                        // finishes before it is added would otherwise never be removed.
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
                    // Per-iteration safety net so one bad item can't kill the loop.
                    // HandleTask doesn't own the semaphore for this item, so release here.
                    if (item != null)
                        item.Status = DownloadItemStatus.Failed;
                    _logger.Error(ex, "Qobuz queue iteration failed; loop continues");
                    semaphore.Release();
                }
            }

            List<Task> remainingTasks;
            lock (_lock)
                remainingTasks = [.. _runningTasks];

            await Task.WhenAll(remainingTasks);
        }

        private async Task HandleTask(DownloadItem item, Task downloadTask, SemaphoreSlim semaphore)
        {
            try
            {
                item.Status = DownloadItemStatus.Downloading;
                await downloadTask;

                if (item.Status == DownloadItemStatus.Completed && !await RunPostProcessAsync(item))
                    item.Status = DownloadItemStatus.Failed;
            }
            catch (OperationCanceledException)
            {
                _logger.Trace("Qobuz download cancelled: {Title}", item.Title);
            }
            catch (Exception ex)
            {
                item.Status = DownloadItemStatus.Failed;
                _logger.Error(ex, "Error while downloading Qobuz album {Title}", item.Title);
            }
            finally
            {
                semaphore.Release();
            }
        }

        private async Task<bool> RunPostProcessAsync(DownloadItem item)
        {
            Album? album = item.RemoteAlbum?.Albums?.FirstOrDefault();
            var request = new PostProcessRequest(
                PostProcessClient.Qobuz,
                item.ID,
                item.Title,
                item.DownloadFolder ?? string.Empty,
                nameof(QobuzDirectDownloadProtocol),
                album);

            return await _postProcess.RunAsync(request, GetTokenForItem(item));
        }
    }
}
