using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Channels;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Plugin.Sleezer.Core.Model;
using NzbDrone.Plugin.Sleezer.Core.PostProcessing;
using NzbDrone.Plugin.Sleezer.Metadata.FFmpeg;
using XabeFFmpeg = Xabe.FFmpeg.FFmpeg;

namespace NzbDrone.Core.Download.Clients.Deezer.Queue
{
    public class DownloadTaskQueue
    {
        // Match SlskdDownloadManager's threshold so behaviour is consistent
        // across download clients.
        private const int CorruptionScanTimeoutSeconds = 120;
        private const double TagConfidenceThreshold = 0.15;

        private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".flac", ".mp3", ".m4a", ".ogg", ".opus", ".wav",
            ".wma", ".aac", ".aiff", ".aif", ".ape", ".wv",
            ".alac", ".m4b", ".m4p", ".mp2", ".mpc", ".dsf", ".dff"
        };

        private readonly Channel<DownloadItem> _queue;
        private readonly List<DownloadItem> _items;
        private readonly Dictionary<DownloadItem, CancellationTokenSource> _cancellationSources;

        private readonly List<Task> _runningTasks = new();
        private readonly object _lock = new();

        private DeezerSettings? _settings;
        private readonly Logger _logger;
        private readonly ICorruptionScanner _corruptionScanner;
        private readonly ICorruptionFailureHandler _corruptionFailureHandler;
        private readonly IPreImportTagger _preImportTagger;
        private readonly IMetadataFactory _metadataFactory;
        private readonly IDiskProvider _diskProvider;
        private bool _ffmpegResolved;
        private int _rehydrated;

        public DownloadTaskQueue(
            int capacity,
            DeezerSettings? settings,
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
            _items = new();
            _cancellationSources = new();
            _settings = settings;
            _corruptionScanner = corruptionScanner;
            _corruptionFailureHandler = corruptionFailureHandler;
            _preImportTagger = preImportTagger;
            _metadataFactory = metadataFactory;
            _diskProvider = diskProvider;
            _logger = logger;
        }

        public void SetSettings(DeezerSettings settings)
        {
            _settings = settings;
            if (Interlocked.CompareExchange(ref _rehydrated, 1, 0) == 0)
                TryRehydrateFromDisk(settings);
        }

        public void StartQueueHandler()
        {
            Task.Run(() => BackgroundProcessing());
        }

        private async Task BackgroundProcessing(CancellationToken stoppingToken = default)
        {
            using SemaphoreSlim semaphore = new(1, 1);

            async Task HandleTask(DownloadItem item, Task task)
            {
                try
                {
                    var token = GetTokenForItem(item);
                    item.EnsureValidity();
                    item.Status = DownloadItemStatus.Downloading;
                    await task;

                    if (item.Status == DownloadItemStatus.Completed)
                        await RunPostProcessAsync(item, token);

                    TryPersistCompletedItem(item);
                }
                catch (TaskCanceledException)
                {
                    _logger.Trace("Deezer download task cancelled: {Title}", item.Title);
                }
                catch (OperationCanceledException)
                {
                    _logger.Trace("Deezer download operation cancelled: {Title}", item.Title);
                }
                catch (Exception ex)
                {
                    item.Status = DownloadItemStatus.Failed;
                    _logger.Error("Error while downloading Deezer album " + item.Title);
                    _logger.Error(ex.ToString());
                }
                finally
                {
                    semaphore.Release();
                    lock (_lock)
                        _runningTasks.Remove(task);
                }
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                await semaphore.WaitAsync(stoppingToken);

                var item = await DequeueAsync(stoppingToken);
                var token = GetTokenForItem(item);
                // SetSettings is always invoked before any queue/download call on the proxy,
                // so by the time we dequeue an item the settings must be populated.
                var downloadTask = item.DoDownload(_settings!, _logger, token);

                lock (_lock)
                    _runningTasks.Add(HandleTask(item, downloadTask));
            }

            List<Task> remainingTasks;
            lock (_lock)
                remainingTasks = _runningTasks.ToList();
            await Task.WhenAll(remainingTasks);
        }

        private async Task RunPostProcessAsync(DownloadItem item, CancellationToken ct)
        {
            FFmpegSettings? sharedSettings = GetSharedPostProcessingSettings();
            bool scanEnabled = sharedSettings?.CorruptionScanClients?.Contains((int)PostProcessClient.Deezer) ?? false;
            bool tagEnabled = sharedSettings?.PreImportTaggingClients?.Contains((int)PostProcessClient.Deezer) ?? false;

            if (!scanEnabled && !tagEnabled)
                return;

            string? folder = item.DownloadFolder;
            if (string.IsNullOrEmpty(folder) || !_diskProvider.FolderExists(folder))
            {
                _logger.Warn($"[post-process] Deezer folder missing for {item.ID}; skipping post-process.");
                return;
            }

            EnsureFFmpegResolved();

            if (scanEnabled)
            {
                List<CorruptionStrike> strikes = await ScanForCorruptAsync(folder, ct);
                if (strikes.Count > 0)
                {
                    // One corrupt file poisons the album. Mark the item Failed,
                    // then delegate to the shared failure handler: it wipes the
                    // whole folder and publishes DownloadFailedEvent so Lidarr
                    // blocklists this release and searches for a different one.
                    item.Status = DownloadItemStatus.Failed;
                    _logger.Warn("[post-process] Deezer item {ID}: {Count} corrupt file(s) found; wiping album and requesting re-search.",
                                 item.ID, strikes.Count);

                    await _corruptionFailureHandler.HandleAsync(
                        downloadId: item.ID,
                        releaseTitle: item.Title,
                        folder: folder,
                        strikes: strikes,
                        protocolName: nameof(DeezerDownloadProtocol),
                        ct: ct);
                    return;
                }
            }

            if (tagEnabled)
            {
                Album? album = item.RemoteAlbum?.Albums?.FirstOrDefault();
                Artist? artist = album?.Artist?.Value;
                if (album == null || artist == null)
                {
                    _logger.Debug($"[post-process] Deezer pre-import tag: skipping {item.ID}; no Album/Artist on RemoteAlbum.");
                    return;
                }

                // Pass null for albumRelease so PreImportTagger lets Lidarr's
                // CandidateService rank releases by track-count distance —
                // forcing the monitored release here is what was causing the
                // "missing tracks" import failures when the download was a
                // different edition than the monitored one.
                await _preImportTagger.TagCompletedDownloadAsync(
                    album,
                    artist,
                    albumRelease: null,
                    item.ID,
                    folder,
                    TagConfidenceThreshold,
                    sharedSettings?.StripFeaturedArtists ?? false,
                    ct);
            }
        }

        private async Task<List<CorruptionStrike>> ScanForCorruptAsync(string folder, CancellationToken ct)
        {
            List<CorruptionStrike> strikes = new();

            string[] audioFiles = _diskProvider.GetFiles(folder, recursive: true)
                .Where(p => AudioExtensions.Contains(Path.GetExtension(p)))
                .ToArray();

            if (audioFiles.Length == 0)
                return strikes;

            int concurrency = Math.Max(2, Environment.ProcessorCount / 2);
            using SemaphoreSlim gate = new(concurrency);

            Task<(string path, CorruptionScanner.Result result)>[] tasks = audioFiles.Select(async path =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    CorruptionScanner.Result r = await _corruptionScanner.ScanAsync(path, CorruptionScanTimeoutSeconds, ct);
                    return (path, r);
                }
                finally { gate.Release(); }
            }).ToArray();

            foreach (Task<(string path, CorruptionScanner.Result result)> t in tasks)
            {
                (string path, CorruptionScanner.Result result) = await t;
                if (!result.IsCorrupt)
                    continue;

                _logger.Warn("[post-process] Deezer corrupt file: {File} — {Reason}", Path.GetFileName(path), result.Reason);
                strikes.Add(new CorruptionStrike(Path.GetFileName(path), result.Reason));
            }

            return strikes;
        }

        private FFmpegSettings? GetSharedPostProcessingSettings()
        {
            try
            {
                return _metadataFactory.All()
                    .Where(d => d.Settings is FFmpegSettings)
                    .Select(d => d.Settings as FFmpegSettings)
                    .FirstOrDefault(s => s != null);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[post-process] Failed to read shared post-processing settings; treating toggles as disabled.");
                return null;
            }
        }

        private void EnsureFFmpegResolved()
        {
            if (_ffmpegResolved)
                return;

            try
            {
                FFmpegSettings? settings = GetSharedPostProcessingSettings();
                if (settings != null && !string.IsNullOrWhiteSpace(settings.FFmpegPath))
                {
                    XabeFFmpeg.SetExecutablesPath(settings.FFmpegPath);
                    AudioMetadataHandler.ResetFFmpegInstallationCheck();
                    _logger.Trace($"[post-process] Applied FFmpeg path: {settings.FFmpegPath}");
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[post-process] Failed to resolve ffmpeg from FFmpeg metadata settings; falling back to PATH lookup.");
            }

            _ffmpegResolved = true;
        }

        public async ValueTask QueueBackgroundWorkItemAsync(DownloadItem workItem)
        {
            await _queue.Writer.WriteAsync(workItem);
            CancellationTokenSource token = new();
            _items.Add(workItem);
            _cancellationSources.Add(workItem, token);
        }

        private async ValueTask<DownloadItem> DequeueAsync(CancellationToken cancellationToken)
        {
            var workItem = await _queue.Reader.ReadAsync(cancellationToken);
            return workItem;
        }

        public void RemoveItem(DownloadItem workItem)
        {
            if (workItem == null)
                return;

            // Rehydrated items were never enqueued, so they have no cancellation source.
            if (_cancellationSources.TryGetValue(workItem, out CancellationTokenSource? src))
            {
                src.Cancel();
                _cancellationSources.Remove(workItem);
            }

            TryDeleteSidecar(workItem);

            _items.Remove(workItem);
        }

        public DownloadItem[] GetQueueListing()
        {
            return _items.ToArray();
        }

        public CancellationToken GetTokenForItem(DownloadItem item)
        {
            if (_cancellationSources.TryGetValue(item, out var src))
                return src!.Token;

            return default;
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
                _logger.Warn(ex, "Failed to persist Deezer download state for {Title}; this download will not survive a Lidarr restart.", item.Title);
            }
        }

        private void TryRehydrateFromDisk(DeezerSettings settings)
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
                        _logger.Debug(ex, "Skipping unreadable Deezer sidecar at {Path}", sidecarPath);
                        continue;
                    }

                    if (persisted == null || persisted.Status != DownloadItemStatus.Completed)
                        continue;

                    if (_items.Any(i => i.ID == persisted.ID))
                        continue;

                    _items.Add(DownloadItem.FromPersisted(persisted));
                    count++;
                }

                if (count > 0)
                    _logger.Info("Rehydrated {Count} completed Deezer download(s) from disk.", count);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to scan Deezer download path for persisted state; starting with an empty queue.");
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
                _logger.Debug(ex, "Failed to delete Deezer sidecar in {Folder}", item.DownloadFolder);
            }
        }
    }
}
