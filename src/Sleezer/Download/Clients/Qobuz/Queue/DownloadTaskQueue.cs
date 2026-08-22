using System;
using System.Collections.Generic;
using System.IO;
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
        private readonly IDiskProvider _diskProvider;
        private readonly PostProcessRunner _postProcess;
        private QobuzSettings? _settings;

        // 0 = rehydration not yet attempted, 1 = attempted. Mirrors Deezer and Tidal.
        private int _rehydrated;

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
            _diskProvider = diskProvider;
            _postProcess = new PostProcessRunner(corruptionScanner, corruptionFailureHandler, preImportTagger, metadataFactory, diskProvider, logger);
        }

        public void SetSettings(QobuzSettings settings)
        {
            _settings = settings;
            if (Interlocked.CompareExchange(ref _rehydrated, 1, 0) == 0)
                TryRehydrateFromDisk(settings);
        }

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

            TryDeleteSidecar(workItem);
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

                TryPersistCompletedItem(item);
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

        // Only completed downloads are persisted: a failed item may have had its files
        // removed by the corrupt-scan pass, so its on-disk state is not an import target.
        private void TryPersistCompletedItem(DownloadItem item)
        {
            if (item.Status != DownloadItemStatus.Completed
                || string.IsNullOrEmpty(item.DownloadFolder)
                || !_diskProvider.FolderExists(item.DownloadFolder))
                return;

            try
            {
                PersistedDownloadItem.CaptureFrom(item).WriteTo(item.DownloadFolder);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to persist Qobuz download state for {Title}; it will not survive a Lidarr restart.", item.Title);
            }
        }

        private void TryRehydrateFromDisk(QobuzSettings settings)
        {
            string? root = settings.DownloadPath;
            if (string.IsNullOrEmpty(root) || !_diskProvider.FolderExists(root))
                return;

            try
            {
                string[] sidecars = Directory.GetFiles(root, PersistedDownloadItem.SidecarFileName, SearchOption.AllDirectories);

                int count = 0;
                foreach (string sidecarPath in sidecars)
                {
                    PersistedDownloadItem? persisted;
                    try
                    {
                        persisted = PersistedDownloadItem.TryRead(sidecarPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "Skipping unreadable Qobuz sidecar at {Path}", sidecarPath);
                        continue;
                    }

                    if (persisted == null || persisted.Status != DownloadItemStatus.Completed)
                        continue;

                    lock (_lock)
                    {
                        if (_items.Any(i => i.ID == persisted.ID))
                            continue;
                        _items.Add(DownloadItem.FromPersisted(persisted));
                    }

                    count++;
                }

                if (count > 0)
                    _logger.Info("Rehydrated {Count} completed Qobuz download(s) from disk.", count);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to scan the Qobuz download path for persisted state; starting with an empty queue.");
            }
        }

        private void TryDeleteSidecar(DownloadItem item)
        {
            if (string.IsNullOrEmpty(item.DownloadFolder))
                return;

            try
            {
                string path = PersistedDownloadItem.SidecarPath(item.DownloadFolder);
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                _logger.Trace(ex, "Failed to delete the Qobuz sidecar for {Title}", item.Title);
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
                nameof(QobuzDownloadProtocol),
                album);

            return await _postProcess.RunAsync(request, GetTokenForItem(item));
        }
    }
}
