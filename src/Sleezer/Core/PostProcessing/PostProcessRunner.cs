using System.Diagnostics;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Music;
using NzbDrone.Plugin.Sleezer.Core.Model;
using NzbDrone.Plugin.Sleezer.Metadata.FFmpeg;
using XabeFFmpeg = Xabe.FFmpeg.FFmpeg;

namespace NzbDrone.Plugin.Sleezer.Core.PostProcessing
{
    /// <summary>
    /// Shared pre-import tagging + corruption-scan pass for the plugin's own download
    /// clients (Deezer, Tidal, Qobuz). Slskd runs the same two steps from its own
    /// manager, which owns extra state this does not model.
    /// </summary>
    public sealed class PostProcessRunner(
        ICorruptionScanner corruptionScanner,
        ICorruptionFailureHandler corruptionFailureHandler,
        IPreImportTagger preImportTagger,
        IMetadataFactory metadataFactory,
        IDiskProvider diskProvider,
        Logger logger,
        Action<string?>? onFfmpegPathResolved = null)
    {
        private const int CorruptionScanTimeoutSeconds = 120;
        private const double TagConfidenceThreshold = 0.15;

        private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".flac", ".mp3", ".m4a", ".ogg", ".opus", ".wav",
            ".wma", ".aac", ".aiff", ".aif", ".ape", ".wv",
            ".alac", ".m4b", ".m4p", ".mp2", ".mpc", ".dsf", ".dff"
        };

        // null = never resolved. Lets a mid-run FFmpeg path change be picked up.
        private string? _lastResolvedFfmpegPath;

        /// <summary>
        /// Runs tagging then scanning for one completed download. Returns false when the
        /// scan condemned the folder and the caller must mark the item failed.
        /// </summary>
        public async Task<bool> RunAsync(PostProcessRequest request, CancellationToken ct)
        {
            FFmpegSettings? sharedSettings = GetSharedSettings();
            bool scanEnabled = sharedSettings?.CorruptionScanClients?.Contains((int)request.Client) ?? false;
            bool tagEnabled = sharedSettings?.PreImportTaggingClients?.Contains((int)request.Client) ?? false;

            if (!scanEnabled && !tagEnabled)
            {
                logger.Debug("[post-process] {Client} item {ID}: scan and tag both disabled; skipping", request.Client, request.DownloadId);
                return true;
            }

            if (string.IsNullOrEmpty(request.Folder) || !diskProvider.FolderExists(request.Folder))
            {
                logger.Warn("[post-process] {Client} folder missing for {ID}; skipping post-process", request.Client, request.DownloadId);
                return true;
            }

            logger.Debug("[post-process] {Client} item {ID}: scan={ScanEnabled} tag={TagEnabled} folder={Folder}",
                request.Client, request.DownloadId, scanEnabled, tagEnabled, request.Folder);

            EnsureFFmpegResolved();

            // Tag first, scan second, so the scan validates the exact bytes Lidarr is
            // about to import rather than the pre-tag ones.
            if (tagEnabled)
                await TagAsync(request, sharedSettings, ct);

            if (!scanEnabled)
                return true;

            var sw = Stopwatch.StartNew();
            List<CorruptionStrike> strikes = await ScanForCorruptAsync(request.Folder, request.Client.ToString(), ct);
            logger.Debug("[post-process] {Client} item {ID}: scan completed in {ElapsedMs}ms — {StrikeCount} strike(s)",
                request.Client, request.DownloadId, sw.ElapsedMilliseconds, strikes.Count);

            if (strikes.Count == 0)
                return true;

            logger.Warn("[post-process] {Client} item {ID}: {Count} corrupt file(s) found; wiping album and requesting re-search",
                request.Client, request.DownloadId, strikes.Count);

            await corruptionFailureHandler.HandleAsync(
                downloadId: request.DownloadId,
                releaseTitle: request.ReleaseTitle,
                folder: request.Folder,
                strikes: strikes,
                protocolName: request.ProtocolName,
                ct: ct);

            return false;
        }

        public FFmpegSettings? GetSharedSettings()
        {
            try
            {
                return metadataFactory.All()
                    .Where(d => d.Settings is FFmpegSettings)
                    .Select(d => d.Settings as FFmpegSettings)
                    .FirstOrDefault(s => s != null);
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "[post-process] Failed to read shared post-processing settings; treating toggles as disabled.");
                return null;
            }
        }

        /// <summary>
        /// Points Xabe.FFmpeg at the configured directory and downloads the binaries if
        /// they aren't there, so the corruption scan has an ffmpeg to call.
        /// </summary>
        public void EnsureFFmpegResolved()
        {
            string? configuredPath = GetSharedSettings()?.FFmpegPath;

            // Throttled (24h), best-effort refresh from chodeus/ffmpeg-static.
            // Fire-and-forget so it never blocks post-process.
            if (!string.IsNullOrWhiteSpace(configuredPath))
                _ = FFmpegInstaller.EnsureUpToDateAsync(configuredPath, logger, CancellationToken.None);

            if (string.Equals(configuredPath, _lastResolvedFfmpegPath, StringComparison.Ordinal))
                return;

            try
            {
                if (string.IsNullOrWhiteSpace(configuredPath))
                {
                    logger.Debug("[post-process] No FFmpeg path configured; the corruption scan will skip files it cannot decode.");
                }
                else
                {
                    XabeFFmpeg.SetExecutablesPath(configuredPath);
                    AudioMetadataHandler.ResetFFmpegInstallationCheck();

                    if (!AudioMetadataHandler.CheckFFmpegInstalled())
                    {
                        logger.Info("[post-process] FFmpeg binaries missing at {Path}; downloading from chodeus/ffmpeg-static", configuredPath);
                        AudioMetadataHandler.InstallFFmpeg(configuredPath).GetAwaiter().GetResult();
                        AudioMetadataHandler.ResetFFmpegInstallationCheck();
                    }

                    logger.Info("[post-process] FFmpeg path applied: {Path}", configuredPath);
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "[post-process] Failed to apply ffmpeg path {Path}; the corruption scan may not run", configuredPath);
            }

            // Clients with their own ffmpeg call path (Tidal's FLAC-from-M4A extraction)
            // need the resolved directory pushed into their wrapper too; without it they
            // fall back to a bare PATH lookup that misses /usr/bin in the Lidarr image.
            onFfmpegPathResolved?.Invoke(string.IsNullOrWhiteSpace(configuredPath) ? null : configuredPath);

            _lastResolvedFfmpegPath = configuredPath;
        }

        private async Task TagAsync(PostProcessRequest request, FFmpegSettings? sharedSettings, CancellationToken ct)
        {
            Album? album = request.Album;
            Artist? artist = album?.Artist?.Value;
            if (album == null || artist == null)
            {
                logger.Debug("[post-process] {Client} pre-import tag: skipping {ID}; no Album/Artist on RemoteAlbum", request.Client, request.DownloadId);
                return;
            }

            logger.Debug("[post-process] {Client} item {ID}: tagging '{Album}' by '{Artist}'", request.Client, request.DownloadId, album.Title, artist.Name);
            var tagSw = Stopwatch.StartNew();

            // albumRelease stays null so Lidarr's CandidateService ranks releases by
            // track-count distance; forcing the monitored release causes spurious
            // "missing tracks" failures when the download is a different edition.
            await preImportTagger.TagCompletedDownloadAsync(
                album,
                artist,
                albumRelease: null,
                request.DownloadId,
                request.Folder,
                TagConfidenceThreshold,
                sharedSettings?.StripFeaturedArtists ?? false,
                ct);

            logger.Debug("[post-process] {Client} item {ID}: tagging completed in {ElapsedMs}ms", request.Client, request.DownloadId, tagSw.ElapsedMilliseconds);
        }

        private async Task<List<CorruptionStrike>> ScanForCorruptAsync(string folder, string clientName, CancellationToken ct)
        {
            List<CorruptionStrike> strikes = [];

            string[] audioFiles = [.. diskProvider.GetFiles(folder, recursive: true).Where(p => AudioExtensions.Contains(Path.GetExtension(p)))];
            if (audioFiles.Length == 0)
                return strikes;

            int concurrency = Math.Max(2, Environment.ProcessorCount / 2);
            using SemaphoreSlim gate = new(concurrency);

            Task<(string Path, CorruptionScanner.Result Result)>[] tasks = [.. audioFiles.Select(async path =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    return (path, await corruptionScanner.ScanAsync(path, CorruptionScanTimeoutSeconds, ct));
                }
                finally
                {
                    gate.Release();
                }
            })];

            foreach (var task in tasks)
            {
                (string path, CorruptionScanner.Result result) = await task;
                if (!result.IsCorrupt)
                    continue;

                logger.Warn("[post-process] {Client} corrupt file: {File} — {Reason}", clientName, Path.GetFileName(path), result.Reason);
                strikes.Add(new CorruptionStrike(Path.GetFileName(path), result.Reason));
            }

            return strikes;
        }
    }

    public sealed record PostProcessRequest(
        PostProcessClient Client,
        string DownloadId,
        string ReleaseTitle,
        string Folder,
        string ProtocolName,
        Album? Album);
}
