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

        private (long id, long size)[] _tracks = null!;
        private DeezerURL _deezerUrl = null!;
        private JToken _deezerAlbum = null!;
        private DateTime _lastARLValidityCheck = DateTime.MinValue;

        public async Task DoDownload(DeezerSettings settings, Logger logger, CancellationToken cancellation = default)
        {
            List<Task> tasks = new();
            using SemaphoreSlim semaphore = new(1, 1);
            foreach (var (trackId, trackSize) in _tracks)
            {
                tasks.Add(Task.Run(async () =>
                {
                    await semaphore.WaitAsync(cancellation);
                    try
                    {
                        await DoTrackDownload(trackId, settings, cancellation);
                        DownloadedSize += trackSize;
                    }
                    catch (TaskCanceledException)
                    {
                        logger.Trace("Track download cancelled: {TrackId}", trackId);
                    }
                    catch (InsufficientLicenseRightsException ex)
                    {
                        logger.Error("Deezer rejected the download: " + ex.Message);
                        logger.Error("This usually means the ARL belongs to a free account. Since March 2025 Deezer requires a Premium/HI-FI account to download tracks.");
                        FailedTracks++;
                    }
                    catch (GeoRestrictionException ex)
                    {
                        logger.Error("Track " + trackId + " is not available in your region: " + ex.Message);
                        FailedTracks++;
                    }
                    catch (Exception ex)
                    {
                        logger.Error("Error while downloading Deezer track " + trackId);
                        logger.Error(ex.ToString());
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
                Status = DownloadItemStatus.Failed;
            else
                Status = DownloadItemStatus.Completed;
        }

        private async Task DoTrackDownload(long track, DeezerSettings settings, CancellationToken cancellation = default)
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

            var ext = Bitrate == Bitrate.FLAC ? "flac" : "mp3";
            var outPath = Path.Combine(settings.DownloadPath, MetadataUtilities.GetFilledTemplate("%albumartist%/%album%/", ext, page, _deezerAlbum), MetadataUtilities.GetFilledTemplate("%track% - %title%.%ext%", ext, page, _deezerAlbum));
            var outDir = Path.GetDirectoryName(outPath)!;

            DownloadFolder = outDir;
            if (!Directory.Exists(outDir))
                Directory.CreateDirectory(outDir);

            try
            {
                await DeezerAPI.Instance.Client.Downloader.WriteRawTrackToFile(track, outPath, Bitrate, null, cancellation);

                if (File.Exists(outPath) && new FileInfo(outPath).Length == 0)
                {
                    File.Delete(outPath);
                    throw new InvalidOperationException($"Deezer returned an empty file for track {track} at {Bitrate}.");
                }
            }
            catch (Exception ex) when (IsLicenseRightsError(ex))
            {
                TryDeleteEmptyFile(outPath);
                throw new InsufficientLicenseRightsException(
                    $"License check failed for track {track} at {Bitrate}: {ex.Message}", ex);
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

            _tracks ??= albumPage["SONGS"]!["data"]!.Select(t => (t["SNG_ID"]!.Value<long>(), t[filesizeKey]!.Value<long>())).ToArray();

            // Defense-in-depth: the parser should not emit a release for a bitrate that any track lacks,
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
            catch { /* best-effort cleanup */ }
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
