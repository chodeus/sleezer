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
        private bool _ffmpegResolved;

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

        public void SetSettings(TidalSettings settings) => _settings = settings;

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
            bool scanEnabled = sharedSettings?.EnableCorruptFileScan ?? false;
            bool tagEnabled = sharedSettings?.EnablePreImportTagging ?? false;

            if (!scanEnabled && !tagEnabled)
                return;

            string? folder = item.DownloadFolder;
            if (string.IsNullOrEmpty(folder) || !_diskProvider.FolderExists(folder))
            {
                _logger.Warn("[post-process] Tidal folder missing for {ID}; skipping post-process.", item.ID);
                return;
            }

            EnsureFFmpegResolved();

            if (scanEnabled)
            {
                List<CorruptionStrike> strikes = await ScanForCorruptAsync(folder, ct);
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

            if (tagEnabled)
            {
                Album? album = item.RemoteAlbum?.Albums?.FirstOrDefault();
                Artist? artist = album?.Artist?.Value;
                if (album == null || artist == null)
                {
                    _logger.Debug("[post-process] Tidal pre-import tag: skipping {ID}; no Album/Artist on RemoteAlbum.", item.ID);
                    return;
                }

                AlbumRelease? albumRelease = album.AlbumReleases?.Value?.FirstOrDefault(r => r.Monitored)
                                              ?? album.AlbumReleases?.Value?.FirstOrDefault();

                await _preImportTagger.TagCompletedDownloadAsync(
                    album,
                    artist,
                    albumRelease,
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
            if (_ffmpegResolved) return;
            try
            {
                FFmpegSettings? settings = GetSharedPostProcessingSettings();
                if (settings != null && !string.IsNullOrWhiteSpace(settings.FFmpegPath))
                {
                    XabeFFmpeg.SetExecutablesPath(settings.FFmpegPath);
                    AudioMetadataHandler.ResetFFmpegInstallationCheck();
                    _logger.Trace("[post-process] Applied FFmpeg path: {Path}", settings.FFmpegPath);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[post-process] Failed to resolve ffmpeg path; falling back to PATH lookup.");
            }
            _ffmpegResolved = true;
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
