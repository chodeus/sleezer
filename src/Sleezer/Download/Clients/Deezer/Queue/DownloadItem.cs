using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeezNET;
using DeezNET.Data;
using DeezNET.Exceptions;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Deezer;

namespace NzbDrone.Core.Download.Clients.Deezer.Queue
{
    public class InsufficientLicenseRightsException : Exception
    {
        public InsufficientLicenseRightsException(string message, Exception? inner = null) : base(message, inner) { }
    }

    public class GeoRestrictionException : Exception
    {
        public GeoRestrictionException(string message, Exception? inner = null) : base(message, inner) { }
    }

    public class DownloadItem
    {
        // Rebuilds the minimal display state for a download that completed in a
        // prior plugin lifetime. The returned item is Lidarr-facing only — it
        // holds no DeezNET handles, no track list, and must never be re-enqueued
        // for download. It exists so GetQueue can report completed downloads
        // that are still on disk after a restart.
        public static DownloadItem FromPersisted(PersistedDownloadItem persisted)
        {
            return new DownloadItem
            {
                ID = persisted.ID,
                Title = persisted.Title,
                Artist = persisted.Artist,
                Explicit = persisted.Explicit,
                Bitrate = persisted.Bitrate,
                TotalSize = persisted.TotalSize,
                DownloadedSize = persisted.TotalSize,
                DownloadFolder = persisted.DownloadFolder,
                Status = persisted.Status,
            };
        }

        public static async Task<DownloadItem?> From(RemoteAlbum remoteAlbum)
        {
            string url = remoteAlbum.Release.DownloadUrl.Trim();
            Bitrate bitrate;

            if (remoteAlbum.Release.Codec == "FLAC")
                bitrate = Bitrate.FLAC;
            else if (remoteAlbum.Release.Container == "320")
                bitrate = Bitrate.MP3_320;
            else
                bitrate = Bitrate.MP3_128;

            DownloadItem? item = null;
            if (DeezerURL.TryParse(url, out var deezerUrl))
            {
                item = new()
                {
                    ID = Guid.NewGuid().ToString(),
                    Status = DownloadItemStatus.Queued,
                    Bitrate = bitrate,
                    RemoteAlbum = remoteAlbum,
                    _deezerUrl = deezerUrl,
                };

                await item.SetDeezerData();
            }

            return item;
        }

        public string ID { get; private set; } = null!;

        public string Title { get; private set; } = null!;
        public string Artist { get; private set; } = null!;
        public bool Explicit { get; private set; }

        public RemoteAlbum RemoteAlbum { get; private set; } = null!;

        public string DownloadFolder { get; private set; } = null!;

        public Bitrate Bitrate { get; private set; }
        public DownloadItemStatus Status { get; set; }

        public float Progress { get => DownloadedSize / (float)Math.Max(TotalSize, 1); }
        public long DownloadedSize { get; private set; }
        public long TotalSize { get; private set; }

        public int FailedTracks { get; private set; }

        private (long id, long size, Bitrate bitrate)[] _tracks = null!;
        private DeezerURL _deezerUrl = null!;
        private JToken _deezerAlbum = null!;
        private DateTime _lastARLValidityCheck = DateTime.MinValue;

        public async Task DoDownload(DeezerSettings settings, Logger logger, CancellationToken cancellation = default)
        {
            var fallbackCount = _tracks.Count(t => t.bitrate != Bitrate);
            if (fallbackCount > 0)
                logger.Info("Deezer download: {FallbackCount} of {TrackCount} track(s) lack {RequestedBitrate}; using MP3 320 for those.",
                    fallbackCount, _tracks.Length, Bitrate);

            List<Task> tasks = new();
            using SemaphoreSlim semaphore = new(1, 1);
            foreach (var (trackId, trackSize, trackBitrate) in _tracks)
            {
                tasks.Add(Task.Run(async () =>
                {
                    await semaphore.WaitAsync(cancellation);
                    try
                    {
                        await DoTrackDownload(trackId, trackBitrate, trackSize, settings, logger, cancellation);
                        DownloadedSize += trackSize;
                    }
                    catch (TaskCanceledException)
                    {
                        logger.Trace("Track download cancelled: {TrackId}", trackId);
                    }
                    catch (InsufficientLicenseRightsException ex)
                    {
                        logger.Error(ex, "Deezer rejected the download for track {TrackId} — ARL likely belongs to a free account. Premium/HI-FI is required as of March 2025.", trackId);
                        FailedTracks++;
                    }
                    catch (GeoRestrictionException ex)
                    {
                        logger.Error(ex, "Track {TrackId} is not available in your region", trackId);
                        FailedTracks++;
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, "Error while downloading Deezer track {TrackId}", trackId);
                        FailedTracks++;
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, cancellation));
            }

            await Task.WhenAll(tasks);

            if (FailedTracks > 0)
            {
                logger.Warn("Deezer album {Title}: {Failed}/{Total} tracks failed; marking item Failed", Title, FailedTracks, _tracks.Length);
                Status = DownloadItemStatus.Failed;

                // Clean up the partial download folder so we don't accumulate
                // half-finished albums under /data/downloads/deezer/. Lidarr
                // won't call our DeleteItemData on Failed items, so we have
                // to do this ourselves here.
                TryCleanupAfterFailure(settings, logger);
            }
            else
            {
                Status = DownloadItemStatus.Completed;
            }
        }

        private void TryCleanupAfterFailure(DeezerSettings settings, Logger logger)
        {
            if (string.IsNullOrEmpty(DownloadFolder))
                return;

            try
            {
                if (Directory.Exists(DownloadFolder))
                {
                    Directory.Delete(DownloadFolder, recursive: true);
                    logger.Debug("Deezer: removed failed-download folder {Folder}", DownloadFolder);
                }

                // Sweep up the now-empty artist folder above us. Same helper
                // the download-client uses on successful imports.
                NzbDrone.Core.Download.Clients.Deezer.Deezer.TryRemoveEmptyParentFolders(
                    DownloadFolder, settings.DownloadPath, logger);
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "Deezer: could not clean up failed-download folder {Folder}", DownloadFolder);
            }
        }

        // Threshold for the partial-write guard. The expected size we get from
        // Deezer's GW API is the catalogued size for the requested bitrate; the
        // FLAC fallback path can legitimately produce a smaller MP3-320 file
        // when a track lacks FLAC, so the threshold must be loose. 0.9 catches
        // catastrophic truncation (network drop, OOM kill, NFS hiccup) without
        // false-positiving on the FLAC→MP3 fallback (the API switches the
        // expected size in that case anyway via the FILESIZE_MP3_320 lookup).
        private const double PartialWriteThreshold = 0.9;

        private async Task DoTrackDownload(long track, Bitrate trackBitrate, long expectedSize, DeezerSettings settings, Logger logger, CancellationToken cancellation = default)
        {
            var page = await DeezerAPI.Instance.Client.GWApi.GetTrackPage(track, cancellation);

            var songTitle = page["DATA"]!["SNG_TITLE"]!.ToString();
            var songVersion = page["DATA"]?["VERSION"]?.ToString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(songVersion))
                songTitle = $"{songTitle} {songVersion}";

            var albumTitle = page["DATA"]!["ALB_TITLE"]!.ToString();
            var albumVersion = _deezerAlbum["DATA"]?["VERSION"]?.ToString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(albumVersion))
                albumTitle = $"{albumTitle} {albumVersion}";

            var artistName = page["DATA"]!["ART_NAME"]!.ToString();
            var duration = page["DATA"]!["DURATION"]!.Value<int>();

            var ext = trackBitrate == Bitrate.FLAC ? "flac" : "mp3";
            var outPath = Path.Combine(settings.DownloadPath, MetadataUtilities.GetFilledTemplate("%albumartist%/%album%/", ext, page, _deezerAlbum), MetadataUtilities.GetFilledTemplate("%track% - %title%.%ext%", ext, page, _deezerAlbum));
            var outDir = Path.GetDirectoryName(outPath)!;

            DownloadFolder = outDir;
            if (!Directory.Exists(outDir))
                Directory.CreateDirectory(outDir);

            try
            {
                await DeezerAPI.Instance.Client.Downloader.WriteRawTrackToFile(track, outPath, trackBitrate, null, cancellation);

                if (File.Exists(outPath) && new FileInfo(outPath).Length == 0)
                {
                    File.Delete(outPath);
                    throw new InvalidOperationException($"Deezer returned an empty file for track {track} at {trackBitrate}.");
                }

                // Partial-write guard. WriteRawTrackToFile awaits the underlying
                // HTTP read-and-write, but a network drop / container OOM /
                // NFS hiccup can leave a non-empty truncated file behind that
                // the empty-file check above won't catch. Compare against the
                // expected size from Deezer's GW API; refuse anything materially
                // smaller and let the caller fail this track loudly rather than
                // shipping a half-track to Lidarr.
                if (expectedSize > 0 && File.Exists(outPath))
                {
                    long actualSize = new FileInfo(outPath).Length;
                    if (actualSize < expectedSize * PartialWriteThreshold)
                    {
                        File.Delete(outPath);
                        throw new InvalidOperationException(
                            $"Deezer track {track} at {trackBitrate} truncated: got {actualSize:N0} of expected {expectedSize:N0} bytes ({(double)actualSize / expectedSize:P0}).");
                    }

                    if (actualSize < expectedSize)
                    {
                        // Above the threshold but still smaller than expected —
                        // log so we can spot a slow drift over time without
                        // breaking downloads. Above the threshold it's almost
                        // always tag-stripping or bitrate-fallback, not real
                        // corruption (the corruption scanner catches that
                        // separately during post-process).
                        logger.Trace("Deezer track {TrackId} at {Bitrate}: got {Actual:N0} of expected {Expected:N0} bytes ({Pct:P0}); within tolerance.",
                            track, trackBitrate, actualSize, expectedSize, (double)actualSize / expectedSize);
                    }
                }
            }
            catch (Exception ex) when (IsLicenseRightsError(ex))
            {
                TryDeleteEmptyFile(outPath);
                throw new InsufficientLicenseRightsException(
                    $"License check failed for track {track} at {trackBitrate}: {ex.Message}", ex);
            }
            catch (Exception ex) when (IsGeoRestrictionError(ex))
            {
                TryDeleteEmptyFile(outPath);
                throw new GeoRestrictionException(
                    $"Track {track} unavailable in your region: {ex.Message}", ex);
            }
            catch
            {
                TryDeleteEmptyFile(outPath);
                throw;
            }

            var plainLyrics = string.Empty;
            List<SyncLyrics>? syncLyrics = null;

            var lyrics = await DeezerAPI.Instance.Client.Downloader.FetchLyricsFromDeezer(track, cancellation);
            if (lyrics.HasValue)
            {
                plainLyrics = lyrics.Value.plainLyrics;

                if (settings.SaveSyncedLyrics)
                    syncLyrics = lyrics.Value.syncLyrics;
            }

            if (settings.UseLRCLIB && (string.IsNullOrWhiteSpace(plainLyrics) || (settings.SaveSyncedLyrics && !(syncLyrics?.Any() ?? false))))
            {
                lyrics = await DeezerAPI.Instance.Client.Downloader.FetchLyricsFromLRCLIB("lrclib.net", songTitle, artistName, albumTitle, duration, cancellation);
                if (lyrics.HasValue)
                {
                    if (string.IsNullOrWhiteSpace(plainLyrics))
                        plainLyrics = lyrics.Value.plainLyrics;
                    if (settings.SaveSyncedLyrics && !(syncLyrics?.Any() ?? false))
                        syncLyrics = lyrics.Value.syncLyrics;
                }
            }

            await DeezerAPI.Instance.Client.Downloader.ApplyMetadataToFile(track, outPath, 512, plainLyrics, token: cancellation);

            if (syncLyrics != null)
                await CreateLrcFile(Path.Combine(outDir, MetadataUtilities.GetFilledTemplate("%track% - %title%.%ext%", "lrc", page, _deezerAlbum)), syncLyrics);

            // TODO: this is currently a waste of resources, if this pr ever gets merged, it can be reenabled
            // https://github.com/Lidarr/Lidarr/pull/4370
            /* try
            {
                string artOut = Path.Combine(outDir, "folder.jpg");
                if (!File.Exists(artOut))
                {
                    byte[] bigArt = await DeezerAPI.Instance.Client.Downloader.GetArtBytes(page["DATA"]!["ALB_PICTURE"]!.ToString(), 1024, cancellation);
                    await File.WriteAllBytesAsync(artOut, bigArt, cancellation);
                }
            }
            catch (UnavailableArtException) { } */
        }

        public void EnsureValidity()
        {
            if ((DateTime.Now - _lastARLValidityCheck).TotalMinutes > 30)
            {
                _lastARLValidityCheck = DateTime.Now;
                var arlValid = ARLUtilities.IsValid(DeezerAPI.Instance.Client.ActiveARL);
                if (!arlValid)
                    throw new InvalidARLException("The applied ARL is not valid for downloading, cannot continue.");
            }
        }

        private async Task SetDeezerData(CancellationToken cancellation = default)
        {
            if (_deezerUrl.EntityType != EntityType.Album)
                throw new InvalidOperationException();

            var albumPage = await DeezerAPI.Instance.Client.GWApi.GetAlbumPage(_deezerUrl.Id, cancellation);

            var filesizeKey = Bitrate switch
            {
                Bitrate.MP3_128 => "FILESIZE_MP3_128",
                Bitrate.MP3_320 => "FILESIZE_MP3_320",
                Bitrate.FLAC => "FILESIZE_FLAC",
                _ => "FILESIZE"
            };

            // Per-track bitrate selection. For FLAC, fall back to MP3 320 when a track lacks FLAC — if the
            // parser emitted this release with AllowMp3FallbackForMissingFlac enabled, the user opted in.
            // For MP3 bitrates, there is no fallback path; a 0-size track stays 0 and trips the guard below.
            _tracks ??= albumPage["SONGS"]!["data"]!.Select(t =>
            {
                var id = t["SNG_ID"]!.Value<long>();
                var primarySize = t[filesizeKey]!.Value<long>();
                if (primarySize > 0 || Bitrate != Bitrate.FLAC)
                    return (id, primarySize, Bitrate);
                var fallbackSize = t["FILESIZE_MP3_320"]!.Value<long>();
                return fallbackSize > 0 ? (id, fallbackSize, Bitrate.MP3_320) : (id, 0L, Bitrate);
            }).ToArray();

            // Defense-in-depth: the parser should not emit a release that leaves any track uncovered,
            // but if that check is ever bypassed, refuse here rather than write 0-byte files to disk.
            var unavailable = _tracks.Count(t => t.size == 0);
            if (unavailable > 0)
                throw new InvalidOperationException(
                    $"{unavailable} of {_tracks.Length} track(s) unavailable at {Bitrate}. Refusing to download a partial album.");

            _deezerAlbum = albumPage;

            var album = albumPage["DATA"]!.ToObject<DeezerGwAlbum>()
                ?? throw new InvalidOperationException($"Deezer returned null DATA block for album {_deezerUrl.Id}");

            Title = album.AlbumTitle;
            Artist = album.ArtistName;
            Explicit = album.Explicit;
            TotalSize = _tracks.Sum(t => t.size);
        }

        private static bool IsLicenseRightsError(Exception ex)
        {
            for (var cur = ex; cur != null; cur = cur.InnerException)
            {
                var msg = cur.Message ?? string.Empty;
                if (msg.Contains("License token has no sufficient rights", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (cur is AggregateException agg && agg.InnerExceptions.Any(IsLicenseRightsError))
                    return true;
            }
            return false;
        }

        private static bool IsGeoRestrictionError(Exception ex)
        {
            for (var cur = ex; cur != null; cur = cur.InnerException)
            {
                var msg = cur.Message ?? string.Empty;
                if (msg.Contains("not available in your country", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("wrong geolocation", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("geo-restricted", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (cur is AggregateException agg && agg.InnerExceptions.Any(IsGeoRestrictionError))
                    return true;
            }
            return false;
        }

        private static void TryDeleteEmptyFile(string path)
        {
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length == 0)
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                NzbDroneLogger.GetLogger(typeof(DownloadItem))
                    .Trace(ex, "Best-effort cleanup of empty file failed at {Path}", path);
            }
        }

        private static async Task CreateLrcFile(string lrcFilePath, List<SyncLyrics> syncLyrics)
        {
            StringBuilder lrcContent = new();
            foreach (var lyric in syncLyrics)
            {
                if (!string.IsNullOrEmpty(lyric.LrcTimestamp) && !string.IsNullOrEmpty(lyric.Line))
                    lrcContent.AppendLine(CultureInfo.InvariantCulture, $"{lyric.LrcTimestamp} {lyric.Line}");
            }
            await File.WriteAllTextAsync(lrcFilePath, lrcContent.ToString());
        }
    }
}
