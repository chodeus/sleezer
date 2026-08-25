using System.Diagnostics;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Music;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using NzbDrone.Plugin.Sleezer.Metadata.FFmpeg;

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
        Logger logger)
    {
        // Per-file ffmpeg decode timeout. 120s handles any real track; a corrupt file
        // that hangs ffmpeg gets killed at this limit.
        internal const int CorruptionScanTimeoutSeconds = 120;

        // Stricter than Lidarr's importer (~0.25): a mis-tag is more permanent than a skip.
        internal const double TagConfidenceThreshold = 0.15;

        private readonly FFmpegPathResolver _ffmpeg = new(logger);

        /// <summary>
        /// Runs tagging then scanning for one completed download. Returns false when the
        /// scan condemned the folder and the caller must mark the item failed.
        /// </summary>
        public async Task<bool> RunAsync(PostProcessRequest request, CancellationToken ct)
        {
            FFmpegSettings? sharedSettings;
            try
            {
                sharedSettings = GetSharedSettings();
            }
            catch (Exception ex)
            {
                logger.Error(ex, "[post-process] {Client} item {ID}: settings unreadable; failing the item rather than importing it unscanned",
                    request.Client, request.DownloadId);
                return false;
            }

            // Absent definition is not the same as "the operator turned both toggles off":
            // the ordinary unconfigured state is a seeded definition with empty client
            // lists. Lidarr seeds one per provider at startup, so null means we cannot tell
            // what was asked for — fail rather than import unverified.
            if (sharedSettings == null)
            {
                logger.Error("[post-process] {Client} item {ID}: no FFmpeg metadata definition found; failing the item rather than importing it unverified",
                    request.Client, request.DownloadId);
                return false;
            }

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

            logger.Info("[post-process] {Client} item {ID}: scan={ScanEnabled} tag={TagEnabled} folder={Folder}",
                request.Client, request.DownloadId, scanEnabled, tagEnabled, request.Folder);

            // Tag first, scan second, so the scan validates the exact bytes Lidarr is
            // about to import rather than the pre-tag ones.
            if (tagEnabled)
                await TagAsync(request, sharedSettings, ct);

            if (!scanEnabled)
                return true;

            // Below the tag gate on purpose: the corruption scan's decode tier is the only
            // thing here that runs ffmpeg, so tagging alone must not trigger an install.
            await EnsureFFmpegResolvedAsync(ct);

            var sw = Stopwatch.StartNew();
            List<CorruptionStrike> strikes = await ScanForCorruptAsync(request.Folder, request.Client.ToString(), ct);
            logger.Info("[post-process] {Client} item {ID}: scan completed in {ElapsedMs}ms — {StrikeCount} strike(s)",
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

        public FFmpegSettings? GetSharedSettings() => ReadSharedSettings(metadataFactory);

        /// <summary>The one reader for the shared post-processing settings.</summary>
        public static FFmpegSettings? ReadSharedSettings(IMetadataFactory factory)
        {
            try
            {
                return factory.All()
                    .Where(d => d.Settings is FFmpegSettings)
                    .Select(d => d.Settings as FFmpegSettings)
                    .FirstOrDefault(s => s != null);
            }
            catch (Exception ex)
            {
                // Deliberately not swallowed: returning null here is indistinguishable
                // from "the operator turned both toggles off", so a transient settings
                // failure would import content that was never scanned or tagged.
                throw new InvalidOperationException("Could not read the shared post-processing settings.", ex);
            }
        }

        /// <summary>Applies the configured FFmpeg directory, fetching the binaries if missing.</summary>
        public Task EnsureFFmpegResolvedAsync(CancellationToken ct) =>
            _ffmpeg.ResolveAsync(GetSharedSettings()?.FFmpegPath, ct);

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

            logger.Info("[post-process] {Client} item {ID}: tagging completed in {ElapsedMs}ms", request.Client, request.DownloadId, tagSw.ElapsedMilliseconds);
        }

        private async Task<List<CorruptionStrike>> ScanForCorruptAsync(string folder, string clientName, CancellationToken ct)
        {
            List<CorruptionStrike> strikes = [];

            string[] audioFiles = [.. diskProvider.GetFiles(folder, recursive: true).Where(AudioFormatHelper.IsPostProcessAudioFile)];
            if (audioFiles.Length == 0)
                return strikes;

            var scanned = await CorruptionScanPass.RunAsync(corruptionScanner, audioFiles, ct);

            foreach ((string path, CorruptionScanner.Result result) in scanned)
            {
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
