using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Core.Qobuz;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using NzbDrone.Plugin.Sleezer.Qobuz;
using QobuzApiSharp.Exceptions;
using QobuzApiSharp.Models.Content;

namespace NzbDrone.Core.Download.Clients.Qobuz.Queue
{
    public class DownloadItem
    {
        private const int MaxAttemptsPerQuality = 3;

        // Anything this small is an error page or a truncated body, not audio.
        private const long MinPlausibleTrackBytes = 50_000;

        private static readonly string DirectoryTemplate = Path.Combine("%albumartist%", "%album%") + Path.DirectorySeparatorChar;
        private const string FileTemplate = "%volume% - %track% - %title%.%ext%";

        private Track[] _tracks = [];
        private QobuzURL _qobuzUrl = null!;
        private Album _qobuzAlbum = null!;
        private byte[]? _albumArt;

        // Mutated by up to MaxConcurrentTracks track tasks at once; a lost increment
        // here would let an incomplete album report Completed.
        private int _completedTracks;
        private int _failedTracks;
        private int _skippedTracks;

        public string ID { get; private set; } = string.Empty;
        public string Title { get; private set; } = string.Empty;
        public string Artist { get; private set; } = string.Empty;
        public bool Explicit { get; private set; }
        public RemoteAlbum RemoteAlbum { get; private set; } = null!;
        public string? DownloadFolder { get; private set; }
        public AudioQuality Bitrate { get; private set; }
        public DownloadItemStatus Status { get; set; }

        public int CompletedTracks => Volatile.Read(ref _completedTracks);
        public int FailedTracks => Volatile.Read(ref _failedTracks);
        public int SkippedTracks => Volatile.Read(ref _skippedTracks);
        public int TrackCount => _tracks.Length;

        /// <summary>Estimated album size from the release, so Lidarr's queue shows bytes.</summary>
        public long TotalSize { get; private set; }

        public long DownloadedSize => TrackCount == 0 ? 0 : TotalSize * CompletedTracks / TrackCount;

        public float Progress => TrackCount == 0 ? 0 : CompletedTracks / (float)TrackCount;

        public static async Task<DownloadItem?> From(RemoteAlbum remoteAlbum)
        {
            var url = remoteAlbum.Release.DownloadUrl?.Trim() ?? string.Empty;
            if (!url.Contains("qobuz", StringComparison.OrdinalIgnoreCase) || !QobuzURL.TryParse(url, out QobuzURL? qobuzUrl))
                return null;

            if (qobuzUrl!.EntityType != QobuzEntityType.Album)
                return null;

            var item = new DownloadItem
            {
                ID = Guid.NewGuid().ToString(),
                Status = DownloadItemStatus.Queued,
                Bitrate = ParseQuality(remoteAlbum.Release.Container),
                RemoteAlbum = remoteAlbum,
                TotalSize = remoteAlbum.Release.Size,
                _qobuzUrl = qobuzUrl,
            };

            await item.LoadAlbum();
            return item;
        }

        public async Task DoDownload(QobuzSettings settings, Logger logger, CancellationToken cancellation = default)
        {
            _albumArt = await FetchAlbumArt(settings, logger, cancellation);

            int concurrency = Math.Clamp(settings.MaxConcurrentTracks, 1, 8);
            using SemaphoreSlim semaphore = new(concurrency, concurrency);

            await Task.WhenAll(_tracks.Select(track => DownloadOneTrack(track, settings, logger, semaphore, cancellation)));

            bool incomplete = FailedTracks > 0
                || CompletedTracks + SkippedTracks < TrackCount
                || (settings.RequireCompleteAlbum && SkippedTracks > 0);

            if (incomplete)
            {
                logger.Warn("Qobuz download incomplete for {Title}: {Completed}/{Total} tracks, {Failed} failed, {Skipped} skipped",
                    Title, CompletedTracks, TrackCount, FailedTracks, SkippedTracks);
                Status = DownloadItemStatus.Failed;
                CleanUpFailedDownload(settings, logger);
                return;
            }

            if (SkippedTracks > 0)
                logger.Warn("Qobuz completed {Title} with {Skipped} track(s) Qobuz does not offer individually", Title, SkippedTracks);

            await WriteCoverSidecar(settings, logger, cancellation);
            Status = DownloadItemStatus.Completed;
        }

        private async Task DownloadOneTrack(Track track, QobuzSettings settings, Logger logger, SemaphoreSlim semaphore, CancellationToken cancellation)
        {
            await semaphore.WaitAsync(cancellation);
            try
            {
                if (track.Streamable == false)
                {
                    logger.Warn("Qobuz track {TrackId} ({TrackTitle}) is not streamable for this account; skipping", track.Id, track.Title);
                    Interlocked.Increment(ref _skippedTracks);
                    return;
                }

                // Qobuz flags hi-res per track, not just per album. Downgrade before the
                // first request rather than burning a 404 on it.
                AudioQuality startingBitrate = Bitrate;
                if (IsHiRes(Bitrate) && track.HiresStreamable == false)
                {
                    logger.Info("Qobuz track {TrackId} ({TrackTitle}) is not hi-res streamable; falling back to FLAC lossless", track.Id, track.Title);
                    startingBitrate = AudioQuality.FLACLossless;
                }

                foreach (AudioQuality quality in GetQualityUpgradeChain(startingBitrate, track))
                {
                    if (await TryDownloadAtQuality(track, quality, settings, logger, cancellation))
                        return;
                }

                logger.Warn("Qobuz track {TrackId} ({TrackTitle}) is unavailable at every quality; skipping", track.Id, track.Title);
                Interlocked.Increment(ref _skippedTracks);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // Item removed from the queue; the queue handler owns the status.
            }
            finally
            {
                semaphore.Release();
            }
        }

        // Returns true when the track is on disk, or when it failed terminally and no
        // further quality should be attempted.
        private async Task<bool> TryDownloadAtQuality(Track track, AudioQuality quality, QobuzSettings settings, Logger logger, CancellationToken cancellation)
        {
            if (quality != Bitrate)
                logger.Info("Qobuz track {TrackId} ({TrackTitle}): retrying at {Quality} instead of {Requested}", track.Id, track.Title, quality, Bitrate);

            for (int attempt = 1; attempt <= MaxAttemptsPerQuality; attempt++)
            {
                try
                {
                    await DoTrackDownload(track.Id.GetValueOrDefault().ToString(CultureInfo.InvariantCulture), quality, settings, cancellation);
                    Interlocked.Increment(ref _completedTracks);
                    return true;
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (ApiErrorResponseException ex) when (ex.ResponseStatusCode == "404")
                {
                    logger.Warn(ex, "Qobuz track {TrackId} ({TrackTitle}) has no file at {Quality}", track.Id, track.Title, quality);
                    return false;
                }
                catch (Exception ex) when (attempt < MaxAttemptsPerQuality)
                {
                    logger.Warn(ex, "Qobuz track {TrackId} ({TrackTitle}) failed at {Quality} (attempt {Attempt}/{Max}); retrying",
                        track.Id, track.Title, quality, attempt, MaxAttemptsPerQuality);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellation);
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "Qobuz track {TrackId} ({TrackTitle}) failed at {Quality} after {Max} attempts",
                        track.Id, track.Title, quality, MaxAttemptsPerQuality);
                    Interlocked.Increment(ref _failedTracks);

                    // A transport failure says nothing about the other quality tiers, so
                    // stop here rather than re-running the whole chain against it.
                    return true;
                }
            }

            return false;
        }

        private async Task DoTrackDownload(string trackId, AudioQuality bitrate, QobuzSettings settings, CancellationToken cancellation)
        {
            QobuzAPI api = QobuzAPI.Instance ?? throw new InvalidOperationException("Qobuz API is not initialised.");

            var page = api.Client.GetTrack(trackId, true);
            var ext = bitrate == AudioQuality.MP3320 ? "mp3" : "flac";

            var outPath = Path.Combine(
                settings.DownloadPath,
                MetadataUtilities.GetFilledTemplate(DirectoryTemplate, ext, page, _qobuzAlbum),
                MetadataUtilities.GetFilledTemplate(FileTemplate, ext, page, _qobuzAlbum));

            var outDir = Path.GetDirectoryName(outPath)!;
            DownloadFolder = outDir;
            Directory.CreateDirectory(outDir);

            await api.Client.WriteRawTrackToFile(trackId, outPath, bitrate, cancellation);

            var fileSize = new FileInfo(outPath).Length;
            if (fileSize < MinPlausibleTrackBytes)
            {
                File.Delete(outPath);
                throw new InvalidOperationException($"Qobuz track {trackId} downloaded only {fileSize} bytes; treating as a failed transfer.");
            }

            (string? plainLyrics, string? syncLyrics) = await FetchLyrics(page, settings, cancellation);

            bool embedArt = (QobuzArtworkPlacement)settings.ArtworkPlacement != QobuzArtworkPlacement.Sidecar;
            await api.Client.ApplyMetadataToFile(trackId, outPath, _albumArt, embedArt, plainLyrics ?? string.Empty, cancellation);

            if (!string.IsNullOrWhiteSpace(syncLyrics))
            {
                var lrcPath = Path.Combine(outDir, MetadataUtilities.GetFilledTemplate(FileTemplate, "lrc", page, _qobuzAlbum));
                await File.WriteAllTextAsync(lrcPath, syncLyrics, cancellation);
            }
        }

        private static async Task<(string? Plain, string? Synced)> FetchLyrics(Track page, QobuzSettings settings, CancellationToken cancellation)
        {
            // Qobuz serves no lyrics of its own, so LRCLIB is the only source here.
            if (!settings.UseLRCLIB)
                return (null, null);

            var lyrics = await QobuzDownloader.FetchLyricsFromLRCLIB(
                "lrclib.net",
                page.CompleteTitle,
                page.Performer?.Name ?? string.Empty,
                page.Album?.CompleteTitle ?? string.Empty,
                page.Duration ?? 0,
                cancellation);

            if (lyrics == null)
                return (null, null);

            return (lyrics.Value.PlainLyrics, settings.SaveSyncedLyrics ? lyrics.Value.SyncLyrics : null);
        }

        private async Task<byte[]?> FetchAlbumArt(QobuzSettings settings, Logger logger, CancellationToken cancellation)
        {
            try
            {
                return await QobuzAPI.Instance!.Client.GetAlbumArtBytes(
                    _qobuzAlbum, (QobuzArtworkSize)settings.ArtworkSize, settings.CustomArtworkResolution, cancellation);
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Qobuz could not fetch album art for {Title}; continuing without it", Title);
                return null;
            }
        }

        private async Task WriteCoverSidecar(QobuzSettings settings, Logger logger, CancellationToken cancellation)
        {
            var placement = (QobuzArtworkPlacement)settings.ArtworkPlacement;
            if (placement == QobuzArtworkPlacement.Embed || _albumArt == null || string.IsNullOrWhiteSpace(DownloadFolder))
                return;

            try
            {
                await File.WriteAllBytesAsync(Path.Combine(DownloadFolder, "cover.jpg"), _albumArt, cancellation);
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Qobuz failed to write cover.jpg for {Title}", Title);
            }
        }

        // A failed album leaves partial tracks Lidarr must not import. Remove them and
        // the shells above, matching what Deezer and Tidal do on failure.
        private void CleanUpFailedDownload(QobuzSettings settings, Logger logger)
        {
            if (string.IsNullOrWhiteSpace(DownloadFolder) || !Directory.Exists(DownloadFolder))
                return;

            try
            {
                Directory.Delete(DownloadFolder, recursive: true);
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "Qobuz could not remove the partial download folder {Folder}", DownloadFolder);
                return;
            }

            DownloadFolderCleanup.TryRemoveEmptyParentFolders(DownloadFolder, settings.DownloadPath, "Qobuz", logger);
        }

        private Task LoadAlbum()
        {
            QobuzAPI api = QobuzAPI.Instance ?? throw new InvalidOperationException("Qobuz API is not initialised.");

            _qobuzAlbum = api.Client.GetAlbum(_qobuzUrl.Id, true);
            _tracks = _qobuzAlbum.Tracks?.Items?.ToArray() ?? [];

            Title = _qobuzAlbum.CompleteTitle;
            Artist = _qobuzAlbum.Artist?.Name ?? string.Empty;
            Explicit = _qobuzAlbum.ParentalWarning.GetValueOrDefault();

            return Task.CompletedTask;
        }

        private static bool IsHiRes(AudioQuality quality)
            => quality is AudioQuality.FLACHiRes24Bit96kHz or AudioQuality.FLACHiRes24Bit192Khz;

        private static AudioQuality ParseQuality(string container) => container switch
        {
            "320" => AudioQuality.MP3320,
            "Lossless" => AudioQuality.FLACLossless,
            "24bit 96kHz" => AudioQuality.FLACHiRes24Bit96kHz,
            "24bit 192kHz" => AudioQuality.FLACHiRes24Bit192Khz,
            _ => AudioQuality.MP3320,
        };

        // Tiers to attempt in order once the per-track hi-res pre-check has run. Handles
        // the remaining case: the requested tier 404s but a higher one exists. Never
        // returns a lower tier, so a Lossless profile can't be satisfied with MP3.
        private static IEnumerable<AudioQuality> GetQualityUpgradeChain(AudioQuality startingQuality, Track track)
        {
            yield return startingQuality;

            switch (startingQuality)
            {
                case AudioQuality.MP3320:
                    yield return AudioQuality.FLACLossless;
                    if (track.HiresStreamable == true)
                    {
                        yield return AudioQuality.FLACHiRes24Bit96kHz;
                        yield return AudioQuality.FLACHiRes24Bit192Khz;
                    }

                    break;

                case AudioQuality.FLACLossless:
                    if (track.HiresStreamable == true)
                    {
                        yield return AudioQuality.FLACHiRes24Bit96kHz;
                        yield return AudioQuality.FLACHiRes24Bit192Khz;
                    }

                    break;

                case AudioQuality.FLACHiRes24Bit96kHz:
                    yield return AudioQuality.FLACHiRes24Bit192Khz;
                    break;

                case AudioQuality.FLACHiRes24Bit192Khz:
                    yield return AudioQuality.FLACHiRes24Bit96kHz;
                    break;
            }
        }
    }
}
