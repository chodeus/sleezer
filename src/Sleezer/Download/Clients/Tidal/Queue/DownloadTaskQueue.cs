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
using XabeFFmpeg = Xabe.FFmpeg.FFmpeg;

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
        private readonly ICorruptionScanner _corruptionScanner;
        private readonly ICorruptionFailureHandler _corruptionFailureHandler;
        private readonly IPreImportTagger _preImportTagger;
        private readonly IMetadataFactory _metadataFactory;
        private readonly IDiskProvider _diskProvider;
        // Track the last FFmpeg path we resolved against so a user changing the
        // FFmpeg metadata path mid-run is picked up on the next post-process.
        // null = never resolved.
        private string? _lastResolvedFfmpegPath;

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
            _corruptionScanner = corruptionScanner;
            _corruptionFailureHandler = corruptionFailureHandler;
            _preImportTagger = preImportTagger;
            _metadataFactory = metadataFactory;
            _diskProvider = diskProvider;
            _logger = logger;
        }

        public void SetSettings(TidalSettings settings)
        {
            _settings = settings;
            // Eagerly resolve the FFmpeg binary path so DownloadItem.HandleAudioConversion
            // (which runs DURING the download, before RunPostProcessAsync would have a
            // chance to call EnsureFFmpegResolved) can find ffprobe / ffmpeg via the
            // path the user configured in Lidarr's FFmpeg metadata settings.
            EnsureFFmpegResolved();
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

                    if (item.Status == DownloadItemStatus.Completed)
                        await RunPostProcessAsync(item, token);

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
                    lock (_lock)
                        _runningTasks.Remove(task);
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

                    lock (_lock)
                        _runningTasks.Add(HandleTask(item, downloadTask));
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

        private async Task RunPostProcessAsync(DownloadItem item, CancellationToken ct)
        {
            FFmpegSettings? sharedSettings = GetSharedPostProcessingSettings();
            bool scanEnabled = sharedSettings?.CorruptionScanClients?.Contains((int)PostProcessClient.Tidal) ?? false;
            bool tagEnabled = sharedSettings?.PreImportTaggingClients?.Contains((int)PostProcessClient.Tidal) ?? false;

            if (!scanEnabled && !tagEnabled)
            {
                _logger.Debug("[post-process] Tidal item {ID}: scan and tag both disabled; skipping", item.ID);
                return;
            }

            string? folder = item.DownloadFolder;
            if (string.IsNullOrEmpty(folder) || !_diskProvider.FolderExists(folder))
            {
                _logger.Warn("[post-process] Tidal folder missing for {ID}; skipping post-process", item.ID);
                return;
            }

            _logger.Debug("[post-process] Tidal item {ID}: scan={ScanEnabled} tag={TagEnabled} folder={Folder}",
                item.ID, scanEnabled, tagEnabled, folder);
            var sw = System.Diagnostics.Stopwatch.StartNew();

            EnsureFFmpegResolved();

            // Tag first, scan second. See the Deezer queue for the rationale —
            // we want the corruption scan to validate the exact bytes Lidarr is
            // about to import, not the pre-tag bytes.
            if (tagEnabled)
            {
                Album? album = item.RemoteAlbum?.Albums?.FirstOrDefault();
                Artist? artist = album?.Artist?.Value;
                if (album == null || artist == null)
                {
                    _logger.Debug("[post-process] Tidal pre-import tag: skipping {ID}; no Album/Artist on RemoteAlbum", item.ID);
                }
                else
                {
                    _logger.Debug("[post-process] Tidal item {ID}: tagging '{Album}' by '{Artist}'", item.ID, album.Title, artist.Name);
                    var tagSw = System.Diagnostics.Stopwatch.StartNew();

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

                    _logger.Debug("[post-process] Tidal item {ID}: tagging completed in {ElapsedMs}ms", item.ID, tagSw.ElapsedMilliseconds);
                }
            }

            if (scanEnabled)
            {
                List<CorruptionStrike> strikes = await ScanForCorruptAsync(folder, ct);
                _logger.Debug("[post-process] Tidal item {ID}: scan completed in {ElapsedMs}ms — {StrikeCount} strike(s)",
                    item.ID, sw.ElapsedMilliseconds, strikes.Count);
                if (strikes.Count > 0)
                {
                    item.Status = DownloadItemStatus.Failed;
                    _logger.Warn("[post-process] Tidal item {ID}: {Count} corrupt file(s) found; wiping album and requesting re-search.",
                                 item.ID, strikes.Count);

                    await _corruptionFailureHandler.HandleAsync(
                        downloadId: item.ID,
                        releaseTitle: item.Title,
                        folder: folder,
                        strikes: strikes,
                        protocolName: nameof(NzbDrone.Core.Indexers.TidalDownloadProtocol),
                        ct: ct);
                    return;
                }
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

            foreach (var t in tasks)
            {
                var (path, result) = await t;
                if (!result.IsCorrupt) continue;
                _logger.Warn("[post-process] Tidal corrupt file: {File} — {Reason}", Path.GetFileName(path), result.Reason);
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
            string? configuredPath = null;
            try
            {
                configuredPath = GetSharedPostProcessingSettings()?.FFmpegPath;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[post-process] Failed to read FFmpeg metadata settings");
            }

            if (string.Equals(configuredPath, _lastResolvedFfmpegPath, StringComparison.Ordinal))
                return;

            try
            {
                if (!string.IsNullOrWhiteSpace(configuredPath))
                {
                    XabeFFmpeg.SetExecutablesPath(configuredPath);
                    AudioMetadataHandler.ResetFFmpegInstallationCheck();

                    // Tidal's audio-conversion path (DownloadItem.HandleAudioConversion)
                    // shells out to ffmpeg/ffprobe directly via our local FFMPEG wrapper
                    // — point it at the same configured directory the rest of sleezer
                    // uses. Without this it falls back to bare PATH lookup which often
                    // misses ffmpeg installed at /usr/bin in the Lidarr container.
                    FFMPEG.SetBinaryDirectory(configuredPath);

                    // If the user has FFmpeg metadata configured but the binaries
                    // aren't actually present at that path (clean Lidarr container
                    // installs are common), trigger the same Xabe.FFmpeg.Downloader
                    // path that FFmpegSettings.OnSet uses. Bounded by InstallFFmpeg's
                    // internal deadline so a stuck Xabe download can't park this
                    // post-process thread forever.
                    if (!AudioMetadataHandler.CheckFFmpegInstalled())
                    {
                        _logger.Info("[post-process] FFmpeg binaries missing at {Path}; downloading via Xabe.FFmpeg.Downloader", configuredPath);
                        try
                        {
                            AudioMetadataHandler.InstallFFmpeg(configuredPath).GetAwaiter().GetResult();
                            AudioMetadataHandler.ResetFFmpegInstallationCheck();
                            _logger.Info("[post-process] FFmpeg auto-install complete at {Path}", configuredPath);
                        }
                        catch (Exception ex)
                        {
                            _logger.Warn(ex, "[post-process] FFmpeg auto-install failed; Tidal conversion options will gracefully no-op until ffmpeg is available at {Path}", configuredPath);
                        }
                    }

                    _logger.Info("[post-process] FFmpeg path applied: {Path}", configuredPath);
                }
                else
                {
                    // Clear the wrapper so a path that was previously set then cleared
                    // doesn't keep leaking through.
                    FFMPEG.SetBinaryDirectory(null);
                    _logger.Debug("[post-process] No FFmpeg path configured in Lidarr metadata settings; Tidal conversion will skip with a Warn if ffmpeg/ffprobe aren't on PATH inside the container.");
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[post-process] Failed to apply ffmpeg path {Path}", configuredPath);
            }

            _lastResolvedFfmpegPath = configuredPath;
        }

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

        private async ValueTask<DownloadItem> DequeueAsync(CancellationToken cancellationToken)
            => await _queue.Reader.ReadAsync(cancellationToken);

        public void RemoveItem(DownloadItem workItem)
        {
            if (workItem == null) return;
            lock (_lock)
            {
                if (_cancellationSources.TryGetValue(workItem, out var src))
                    src.Cancel();
                _items.Remove(workItem);
                _cancellationSources.Remove(workItem);
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
