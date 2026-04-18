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
        private readonly IPreImportTagger _preImportTagger;
        private readonly IMetadataFactory _metadataFactory;
        private readonly IDiskProvider _diskProvider;
        private bool _ffmpegResolved;

        public DownloadTaskQueue(
            int capacity,
            DeezerSettings? settings,
            ICorruptionScanner corruptionScanner,
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
            _preImportTagger = preImportTagger;
            _metadataFactory = metadataFactory;
            _diskProvider = diskProvider;
            _logger = logger;
        }

        public void SetSettings(DeezerSettings settings) => _settings = settings;

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
            bool scanEnabled = sharedSettings?.EnableCorruptFileScan ?? false;
            bool tagEnabled = sharedSettings?.EnablePreImportTagging ?? false;

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
                int corruptCount = await ScanAndDeleteCorruptAsync(item, folder, ct);
                if (corruptCount > 0)
                {
                    // Deezer downloads are deterministic, so a corrupt file would just
                    // recur on retry. Mark the album failed so Lidarr doesn't import the
                    // partial set; deleted corrupt files are gone, the rest stay on disk
                    // for the user to review.
                    item.Status = DownloadItemStatus.Failed;
                    _logger.Warn($"[post-process] Deezer item {item.ID}: {corruptCount} corrupt file(s) deleted; marking download Failed.");
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

        private async Task<int> ScanAndDeleteCorruptAsync(DownloadItem item, string folder, CancellationToken ct)
        {
            string[] audioFiles = _diskProvider.GetFiles(folder, recursive: true)
                .Where(p => AudioExtensions.Contains(Path.GetExtension(p)))
                .ToArray();

            if (audioFiles.Length == 0)
                return 0;

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

            int corruptCount = 0;
            foreach (Task<(string path, CorruptionScanner.Result result)> t in tasks)
            {
                (string path, CorruptionScanner.Result result) = await t;
                if (!result.IsCorrupt)
                    continue;

                corruptCount++;
                _logger.Warn($"[post-process] Deezer corrupt file: {Path.GetFileName(path)} \u2014 {result.Reason}");

                try { _diskProvider.DeleteFile(path); }
                catch (Exception ex) { _logger.Warn(ex, $"[post-process] Failed to delete corrupt file {path}"); }
            }

            return corruptCount;
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

            _cancellationSources[workItem].Cancel();

            _items.Remove(workItem);
            _cancellationSources.Remove(workItem);
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
    }
}
