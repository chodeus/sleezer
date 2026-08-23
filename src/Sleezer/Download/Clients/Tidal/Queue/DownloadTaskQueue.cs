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
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Core.Model;
using NzbDrone.Plugin.Sleezer.Core.PostProcessing;
using NzbDrone.Plugin.Sleezer.Metadata.FFmpeg;

namespace NzbDrone.Core.Download.Clients.Tidal.Queue
{
    public class DownloadTaskQueue
    {
        // Match Slskd / Deezer threshold so behaviour is consistent across
        // all sleezer-managed download clients.
        private const int CorruptionScanTimeoutSeconds = 120;
        private const double TagConfidenceThreshold = 0.15;

        private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".flac", ".mp3", ".m4a", ".ogg", ".opus", ".wav",
            ".wma", ".aac", ".aiff", ".aif", ".ape", ".wv",
            ".alac", ".m4b", ".m4p", ".mp2", ".mpc", ".dsf", ".dff"
        };

        private readonly Channel<DownloadItem> _queue;
        private readonly List<DownloadItem> _items = new();
        private readonly Dictionary<DownloadItem, CancellationTokenSource> _cancellationSources = new();
        private readonly List<Task> _runningTasks = new();
        private readonly object _lock = new();

        private TidalSettings? _settings;
        private readonly Logger _logger;
        private readonly PostProcessRunner _postProcess;
        private readonly IDiskProvider _diskProvider;

        // 0 = rehydration not yet attempted, 1 = attempted (idempotent). Mirrors
        // the Deezer queue's _rehydrated guard.
        private int _rehydrated;

        public DownloadTaskQueue(
            int capacity,
            TidalSettings? settings,
            ICorruptionScanner corruptionScanner,
            ICorruptionFailureHandler corruptionFailureHandler,
            IPreImportTagger preImportTagger,
            IMetadataFactory metadataFactory,
            IDiskProvider diskProvider,
            Logger logger)
        {
            BoundedChannelOptions options = new(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait
            };
            _queue = Channel.CreateBounded<DownloadItem>(options);
            _settings = settings;
            _diskProvider = diskProvider;
            _logger = logger;
            _postProcess = new PostProcessRunner(corruptionScanner, corruptionFailureHandler, preImportTagger, metadataFactory, diskProvider, logger, FFMPEG.SetBinaryDirectory);
        }

        public void SetSettings(TidalSettings settings)
        {
            _settings = settings;
            // Eagerly resolve the FFmpeg binary path so DownloadItem.HandleAudioConversion
            // (which runs DURING the download, before RunPostProcessAsync would have a
            // chance to call EnsureFFmpegResolved) can find ffprobe / ffmpeg via the
            // path the user configured in Lidarr's FFmpeg metadata settings.
            _postProcess.EnsureFFmpegResolved();
            if (Interlocked.CompareExchange(ref _rehydrated, 1, 0) == 0)
                TryRehydrateFromDisk(settings);
        }

        public void StartQueueHandler() => Task.Run(() => BackgroundProcessing());

        private async Task BackgroundProcessing(CancellationToken stoppingToken = default)
        {
            using SemaphoreSlim semaphore = new(3, 3);

            async Task HandleTask(DownloadItem item, Task task)
            {
                try
                {
                    var token = GetTokenForItem(item);
                    item.Status = DownloadItemStatus.Downloading;
                    await task;

                    if (item.Status == DownloadItemStatus.Completed && !await RunPostProcessAsync(item, token))
                        item.Status = DownloadItemStatus.Failed;

                    TryPersistCompletedItem(item);
                }
                catch (TaskCanceledException)
                {
                    _logger.Trace("Tidal download task cancelled: {Title}", item.Title);
                }
                catch (OperationCanceledException)
                {
                    _logger.Trace("Tidal download operation cancelled: {Title}", item.Title);
                }
                catch (Exception ex)
                {
                    item.Status = DownloadItemStatus.Failed;
                    _logger.Error(ex, "Error while downloading Tidal album {Title}", item.Title);
                }
                finally
                {
                    semaphore.Release();

                    // The source stays alive through RemoveItem so the worker always has
                    // a real token; this is the only place that knows the work is over.
                    lock (_lock)
                    {
                        if (_cancellationSources.Remove(item, out var src))
                            src.Dispose();
                    }
                }
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                await semaphore.WaitAsync(stoppingToken);

                DownloadItem? item = null;
                try
                {
                    item = await DequeueAsync(stoppingToken);

                    if (_settings == null)
                    {
                        // Settings not yet propagated. Drop the item rather
                        // than crash the loop; the proxy always SetSettings
                        // before queueing, so this is genuinely unexpected.
                        _logger.Error("Tidal queue received item before settings populated; marking failed: {Title}", item.Title);
                        item.Status = DownloadItemStatus.Failed;
                        semaphore.Release();
                        continue;
                    }

                    var token = GetTokenForItem(item);
                    var downloadTask = item.DoDownload(_settings, _logger, token);

                    var handler = HandleTask(item, downloadTask);
                    lock (_lock)
                    {
                        // Pruned here, not inside HandleTask: a handler that finished
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
                    // Per-iteration safety net so one bad item can't kill the
                    // whole background loop. The semaphore must always be
                    // released since HandleTask isn't owning it for this item.
                    if (item != null)
                        item.Status = DownloadItemStatus.Failed;
                    _logger.Error(ex, "Tidal queue iteration failed; loop continues");
                    semaphore.Release();
                }
            }

            List<Task> remainingTasks;
            lock (_lock)
                remainingTasks = _runningTasks.ToList();
            await Task.WhenAll(remainingTasks);
        }


        private async Task<bool> RunPostProcessAsync(DownloadItem item, CancellationToken ct)
        {
            Album? album = item.RemoteAlbum?.Albums?.FirstOrDefault();
            var request = new PostProcessRequest(
                PostProcessClient.Tidal,
                item.ID,
                item.Title,
                item.DownloadFolder ?? string.Empty,
                nameof(NzbDrone.Core.Indexers.TidalDownloadProtocol),
                album);

            return await _postProcess.RunAsync(request, ct);
        }

        public async ValueTask QueueBackgroundWorkItemAsync(DownloadItem workItem)
        {
            // Register before the channel write: the consumer can dequeue and call
            // GetTokenForItem the instant the write lands, and an unregistered item
            // yields default(CancellationToken) — one that can never be cancelled, so
            // RemoveItem would drop it from the queue while the download kept running.
            CancellationTokenSource token = new();
            lock (_lock)
            {
                _items.Add(workItem);
                _cancellationSources.Add(workItem, token);
            }

            // Publish under the item's own token so an item cancelled while waiting for
            // capacity never reaches a worker at all.
            await _queue.Writer.WriteAsync(workItem, token.Token);
        }

        private async ValueTask<DownloadItem> DequeueAsync(CancellationToken cancellationToken)
            => await _queue.Reader.ReadAsync(cancellationToken);

        public void RemoveItem(DownloadItem workItem)
        {
            if (workItem == null) return;
            lock (_lock)
            {
                if (_cancellationSources.TryGetValue(workItem, out var src))
                {
                    // Cancel but keep the mapping — see the Qobuz queue.
                    src.Cancel();
                }

                _items.Remove(workItem);
            }
            TryDeleteSidecar(workItem);
        }

        private void TryPersistCompletedItem(DownloadItem item)
        {
            // Only completed downloads are persisted. Failed/cancelled items
            // may have had files deleted by the corrupt-scan pass, so their
            // on-disk state isn't a valid import target.
            if (item.Status != DownloadItemStatus.Completed)
                return;

            if (string.IsNullOrEmpty(item.DownloadFolder) || !_diskProvider.FolderExists(item.DownloadFolder))
                return;

            try
            {
                PersistedDownloadItem.CaptureFrom(item).WriteTo(item.DownloadFolder);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to persist Tidal download state for {Title}; this download will not survive a Lidarr restart.", item.Title);
            }
        }

        private void TryRehydrateFromDisk(TidalSettings settings)
        {
            string? root = settings.DownloadPath;
            if (string.IsNullOrEmpty(root) || !_diskProvider.FolderExists(root))
                return;

            try
            {
                string[] sidecars = Directory.GetFiles(
                    root,
                    PersistedDownloadItem.SidecarFileName,
                    SearchOption.AllDirectories);

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
                        _logger.Debug(ex, "Skipping unreadable Tidal sidecar at {Path}", sidecarPath);
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
                    _logger.Info("Rehydrated {Count} completed Tidal download(s) from disk.", count);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to scan Tidal download path for persisted state; starting with an empty queue.");
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
                _logger.Trace(ex, "Failed to delete Tidal sidecar for {Title}", item.Title);
            }
        }

        public DownloadItem[] GetQueueListing()
        {
            lock (_lock)
                return _items.ToArray();
        }

        public CancellationToken GetTokenForItem(DownloadItem item)
        {
            lock (_lock)
                return _cancellationSources.TryGetValue(item, out var src) ? src.Token : default;
        }
    }
}
