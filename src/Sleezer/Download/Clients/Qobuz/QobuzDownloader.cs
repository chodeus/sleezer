using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

// ImagesTools is the plugin's only image scaler; it lives in the Deezer namespace
// for historical reasons (see CLAUDE.md namespace notes), not because it's Deezer-only.
using NzbDrone.Plugin.Sleezer.Deezer;
using NzbDrone.Plugin.Sleezer.Qobuz;
using QobuzApiSharp.Models.Content;
using QobuzApiSharp.Service;

namespace NzbDrone.Core.Download.Clients.Qobuz
{
    public static class QobuzDownloader
    {
        // A single stalled attempt still has to fail so the caller can retry, but a
        // 24/192 track legitimately outruns HttpClient's 100s default, so the bound
        // is per-attempt via a linked token rather than a client-wide timeout.
        private static readonly TimeSpan AttemptTimeout = TimeSpan.FromMinutes(10);

        // Lyrics and artwork are optional extras, so they get a far shorter leash than
        // a track body — a stalled one otherwise holds a track slot indefinitely.
        private static readonly TimeSpan AuxRequestTimeout = TimeSpan.FromSeconds(30);

        private static readonly HttpClient _client = new() { Timeout = Timeout.InfiniteTimeSpan };

        // static.qobuz.com/.../{id}_600.jpg -> _org.jpg (Qobuz's maximum resolution).
        private static readonly Regex ArtSizeSuffix = new(@"_\d+\.jpg$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static async Task WriteRawTrackToFile(this QobuzApiService s, string trackId, string trackPath, AudioQuality bitrate, CancellationToken token = default)
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            attemptCts.CancelAfter(AttemptTimeout);
            var attemptToken = attemptCts.Token;

            using HttpResponseMessage response = await s.GetTrackResponse(trackId, bitrate, attemptToken);
            long? expectedLength = response.Content.Headers.ContentLength;

            // Stream to a temp file and only move it into place once fully written and
            // length-checked, so an interrupted download never leaves a truncated file
            // where Lidarr would import it.
            var tempPath = trackPath + ".part";
            try
            {
                await using (FileStream fileStream = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                await using (Stream httpStream = await response.Content.ReadAsStreamAsync(attemptToken))
                {
                    await httpStream.CopyToAsync(fileStream, attemptToken);
                    await fileStream.FlushAsync(attemptToken);
                }

                if (expectedLength.HasValue)
                {
                    long actualLength = new FileInfo(tempPath).Length;
                    if (actualLength != expectedLength.Value)
                        throw new IOException($"Incomplete download for Qobuz track {trackId}: server reported {expectedLength.Value} bytes but {actualLength} were written.");
                }

                File.Move(tempPath, trackPath, overwrite: true);
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }
        }

        public static async Task<(string? PlainLyrics, string? SyncLyrics)?> FetchLyricsFromLRCLIB(string instance, string trackName, string artistName, string albumName, long duration, CancellationToken token = default)
        {
            var requestUrl = $"https://{instance}/api/get?artist_name={Uri.EscapeDataString(artistName)}&track_name={Uri.EscapeDataString(trackName)}&album_name={Uri.EscapeDataString(albumName)}&duration={duration}";

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(AuxRequestTimeout);

            var response = await _client.GetAsync(requestUrl, cts.Token);

            if (!response.IsSuccessStatusCode)
                return null;

            var content = await response.Content.ReadAsStringAsync(token);
            var json = JObject.Parse(content);
            return (json["plainLyrics"]?.ToString(), json["syncedLyrics"]?.ToString());
        }

        /// <summary>Album cover at the configured size, or null when Qobuz has none.</summary>
        public static async Task<byte[]?> GetAlbumArtBytes(this QobuzApiService s, Album? albumData, QobuzArtworkSize size, int customResolution, CancellationToken token = default)
        {
            var image = albumData?.Image;
            if (image == null)
                return null;

            string url = size switch
            {
                QobuzArtworkSize.Small => image.Small,

                // Original and Custom both source the full-resolution image.
                QobuzArtworkSize.Large => image.Large,
                _ => ToOriginalUrl(image.Large),
            };

            byte[]? bytes = await TryFetchArt(url, token)
                            ?? await TryFetchArt(image.Large, token)
                            ?? await TryFetchArt(image.Small, token);

            if (bytes != null && size == QobuzArtworkSize.Custom && customResolution > 0)
                bytes = ImagesTools.Scale(bytes, customResolution, customResolution);

            return bytes;
        }

        public static async Task ApplyMetadataToFile(this QobuzApiService s, string trackId, string trackPath, byte[]? albumArt, bool embedArt, string lyrics = "", CancellationToken token = default)
        {
            using TagLib.File file = TagLib.File.Create(trackPath);
            await Task.Run(() => s.ApplyMetadataToTagLibFile(file, trackId, albumArt, embedArt, lyrics), token);
        }

        private static string ToOriginalUrl(string largeUrl)
            => string.IsNullOrEmpty(largeUrl) ? largeUrl : ArtSizeSuffix.Replace(largeUrl, "_org.jpg");

        private static async Task<byte[]?> TryFetchArt(string url, CancellationToken token)
        {
            if (string.IsNullOrEmpty(url))
                return null;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(AuxRequestTimeout);

            using HttpRequestMessage message = new(HttpMethod.Get, url);
            using HttpResponseMessage response = await _client.SendAsync(message, cts.Token);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(cts.Token) : null;
        }

        // Runs inside a catch block, so it must not throw: doing so would replace the
        // original download exception that the caller branches on. File.Delete already
        // no-ops on a missing file, so the Exists check was only a TOCTOU race.
        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static async Task<HttpResponseMessage> GetTrackResponse(this QobuzApiService s, string trackId, AudioQuality bitrate, CancellationToken token = default)
        {
            var urls = s.GetTrackFileUrl(trackId, ((int)bitrate).ToString())
                ?? throw new InvalidOperationException($"Qobuz track {trackId} has no media source at {bitrate}.");

            if (urls.Sample ?? false)
                throw new InvalidOperationException($"Qobuz returned a 30-second sample for track {trackId} — the account's subscription does not cover {bitrate}.");

            HttpRequestMessage message = new(HttpMethod.Get, urls.Url);

            // ResponseHeadersRead: stream to disk rather than buffering an often
            // 50-150 MB hi-res file in memory, with several tracks running at once.
            HttpResponseMessage response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);

            try
            {
                // Without this an expired/403 CDN URL would write its error body straight
                // into the .flac.
                response.EnsureSuccessStatusCode();
            }
            catch
            {
                // The caller's `using` never takes ownership when this throws, and the
                // download path retries, so the leak would repeat per attempt.
                response.Dispose();
                throw;
            }

            return response;
        }

        private static void ApplyMetadataToTagLibFile(this QobuzApiService s, TagLib.File track, string trackId, byte[]? albumArt, bool embedArt, string lyrics)
        {
            var page = s.GetTrack(trackId, true);
            var albumPage = page.Album?.Id is string albumId ? s.GetAlbum(albumId, true) : null;

            track.Tag.Title = page.CompleteTitle;
            track.Tag.Album = albumPage?.CompleteTitle ?? page.Album?.CompleteTitle;

            if (page.Performer?.Name is string performer)
                track.Tag.Performers = [performer];

            if (albumPage?.Artists is { } artists)
                track.Tag.AlbumArtists = [.. artists.Select(x => x.Name)];
            track.Tag.Year = (uint)page.ReleaseDateOriginal.GetValueOrDefault().DateTime.Year;
            track.Tag.Track = (uint)page.TrackNumber.GetValueOrDefault();
            track.Tag.TrackCount = (uint)(albumPage?.TracksCount).GetValueOrDefault();
            track.Tag.Disc = (uint)page.MediaNumber.GetValueOrDefault();
            track.Tag.DiscCount = (uint)(albumPage?.MediaCount).GetValueOrDefault();

            if (albumPage?.Genre?.Name is { Length: > 0 } genre)
                track.Tag.Genres = [genre];

            if (embedArt && albumArt != null)
                track.Tag.Pictures = [new TagLib.Picture(new TagLib.ByteVector(albumArt))];

            track.Tag.Lyrics = lyrics;
            track.Save();
        }
    }
}
