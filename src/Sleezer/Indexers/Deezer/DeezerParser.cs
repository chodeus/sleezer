using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download.Clients.Deezer;
using NzbDrone.Core.Parser.Model;
using System.Collections.Concurrent;
using NzbDrone.Plugin.Sleezer.Deezer;
using System.Globalization;

namespace NzbDrone.Core.Indexers.Deezer
{
    public class DeezerParser : IParseIndexerResponse
    {
        private static readonly Regex ParenToDash = new(@"\s*\(([^)]+)\)", RegexOptions.Compiled);
        private static readonly Regex CollapseSpaces = new(@"\s+", RegexOptions.Compiled);
        // Bounds the blocking wait in ParseResponse. A Deezer search + album enrichment fan-out
        // that can't finish in this window is almost certainly a hung HTTP call — better to throw
        // than to wedge the indexer thread forever.
        private static readonly TimeSpan IndexerParseTimeout = TimeSpan.FromMinutes(2);

        public DeezerIndexerSettings Settings { get; set; } = null!;

        public IList<ReleaseInfo> ParseResponse(IndexerResponse response)
        {
            var torrentInfos = new List<ReleaseInfo>();

            DeezerSearchResponse jsonResponse;
            if (response.HttpRequest.Url.FullUri.Contains("method=page.get", StringComparison.InvariantCulture)) // means we're asking for a channel and need to parse it accordingly
            {
                var task = GenerateSearchResponseFromChannelData(response.Content);
                if (!task.Wait(IndexerParseTimeout))
                    throw new TimeoutException($"Deezer channel parse did not complete within {IndexerParseTimeout.TotalSeconds:F0}s");
                jsonResponse = task.Result;
            }
            else
                jsonResponse = new HttpResponse<DeezerSearchResponseWrapper>(response.HttpResponse).Resource.Results;

            var tasks = jsonResponse.Data.Select(result => ProcessResultAsync(result)).ToArray();

            if (!Task.WaitAll(tasks, IndexerParseTimeout))
                throw new TimeoutException($"Deezer search enrichment did not complete within {IndexerParseTimeout.TotalSeconds:F0}s");

            foreach (var task in tasks)
            {
                if (task.Result != null)
                    torrentInfos.AddRange(task.Result);
            }
            
            return torrentInfos
                .OrderByDescending(o => o.Size)
                .ToArray();
        }

        private async Task<IList<ReleaseInfo>?> ProcessResultAsync(DeezerGwAlbum result)
        {
            var torrentInfos = new List<ReleaseInfo>();

            var albumPage = await DeezerAPI.Instance.Client.GWApi.GetAlbumPage(long.Parse(result.AlbumId, CultureInfo.InvariantCulture));
            var songs = albumPage["SONGS"]!["data"]!;

            // Per-format availability: a track with FILESIZE_X == 0 is unavailable at that format.
            // Emitting a release for a format where some tracks are missing produces 0-byte files at download time.
            var missing128 = songs.Any(d => d["FILESIZE_MP3_128"]!.Value<long>() == 0);
            var missing320 = songs.Any(d => d["FILESIZE_MP3_320"]!.Value<long>() == 0);
            var missingFlac = songs.Any(d => d["FILESIZE_FLAC"]!.Value<long>() == 0);
            // For the MP3-320 fallback: every track must be available in FLAC or MP3 320 (or both),
            // otherwise we can't cover the album even with fallback enabled.
            var flacOrMp3320CoversAll = songs.All(d => d["FILESIZE_FLAC"]!.Value<long>() > 0 || d["FILESIZE_MP3_320"]!.Value<long>() > 0);

            if (Settings.HideAlbumsWithMissing && missing128)
                return null;

            // Album-level explicit status is sometimes wrong; cross-check against per-track EXPLICIT_LYRICS
            // when the album claims non-explicit but most tracks disagree.
            var explicitType = result.ExplicitType;
            if (explicitType == ExplicitStatus.NotExplicit || explicitType == ExplicitStatus.Unknown)
            {
                var trackStatuses = songs.Select(t => t["EXPLICIT_LYRICS"]?.Value<int>() ?? 2).ToList();
                if (trackStatuses.Count > 0)
                {
                    var cleanCount = trackStatuses.Count(s => s == 3);
                    var explicitCount = trackStatuses.Count(s => s == 1 || s == 4);
                    if (cleanCount * 2 > trackStatuses.Count)
                        explicitType = ExplicitStatus.Clean;
                    else if (explicitCount * 2 > trackStatuses.Count)
                        explicitType = ExplicitStatus.Explicit;
                }
            }

            if (Settings.HideCleanReleases && explicitType == ExplicitStatus.Clean)
                return null;

            var size128 = songs.Sum(d => d["FILESIZE_MP3_128"]!.Value<long>());
            var size320 = songs.Sum(d => d["FILESIZE_MP3_320"]!.Value<long>());
            var sizeFlac = songs.Sum(d => d["FILESIZE_FLAC"]!.Value<long>());

            // MP3 128 — always available baseline (unless filtered by HideAlbumsWithMissing above)
            if (!missing128)
                torrentInfos.Add(ToReleaseInfo(result, 1, size128, explicitType));

            // MP3 320 — only if user can stream HQ AND all tracks have MP3 320
            if (!missing320 && DeezerAPI.Instance.Client.GWApi.ActiveUserData?["USER"]?["OPTIONS"]?["web_hq"]?.Value<bool>() == true)
            {
                torrentInfos.Add(ToReleaseInfo(result, 3, size320, explicitType));
            }

            // FLAC — only if user has lossless AND all tracks have FLAC,
            // OR (with fallback opt-in) every track is available in at least one of FLAC/MP3 320 and user has both HQ streaming and lossless.
            var hasLossless = DeezerAPI.Instance.Client.GWApi.ActiveUserData?["USER"]?["OPTIONS"]?["web_lossless"]?.Value<bool>() == true;
            var hasHq = DeezerAPI.Instance.Client.GWApi.ActiveUserData?["USER"]?["OPTIONS"]?["web_hq"]?.Value<bool>() == true;
            if (!missingFlac && hasLossless)
            {
                torrentInfos.Add(ToReleaseInfo(result, 9, sizeFlac, explicitType));
            }
            else if (missingFlac && Settings.AllowMp3FallbackForMissingFlac && flacOrMp3320CoversAll && hasLossless && hasHq)
            {
                // Size reflects what will actually be downloaded: FLAC where available, MP3 320 otherwise.
                var sizeMixed = songs.Sum(d =>
                {
                    var flacBytes = d["FILESIZE_FLAC"]!.Value<long>();
                    return flacBytes > 0 ? flacBytes : d["FILESIZE_MP3_320"]!.Value<long>();
                });
                torrentInfos.Add(ToReleaseInfo(result, 9, sizeMixed, explicitType));
            }

            return torrentInfos;
        }

        private static ReleaseInfo ToReleaseInfo(DeezerGwAlbum x, int bitrate, long size, ExplicitStatus explicitType)
        {
            var publishDate = DateTime.UtcNow;
            var year = 0;
            if (DateTime.TryParse(x.DigitalReleaseDate, out var digitalReleaseDate))
            {
                publishDate = digitalReleaseDate;
                year = publishDate.Year;
            }
            else if (DateTime.TryParse(x.PhysicalReleaseDate, out var physicalReleaseDate))
            {
                publishDate = physicalReleaseDate;
                year = publishDate.Year;
            }

            var url = $"https://deezer.com/album/{x.AlbumId}";

            var result = new ReleaseInfo
            {
                Guid = $"Deezer-{x.AlbumId}-{bitrate}",
                Artist = x.ArtistName,
                Album = x.AlbumTitle,
                DownloadUrl = url,
                InfoUrl = url,
                PublishDate = publishDate,
                DownloadProtocol = nameof(DeezerDownloadProtocol)
            };

            string format;
            switch (bitrate)
            {
                case 9:
                    result.Codec = "FLAC";
                    result.Container = "Lossless";
                    format = "FLAC";
                    break;
                case 3:
                    result.Codec = "MP3";
                    result.Container = "320";
                    format = "MP3 320";
                    break;
                case 1:
                    result.Codec = "MP3";
                    result.Container = "128";
                    format = "MP3 128";
                    break;
                default:
                    throw new NotImplementedException();
            }

            result.Size = size;
            // Parens inside the album name collide with Lidarr's release-title parser, which captures album
            // non-greedily up to the first "(" or "[". Convert "(foo)" to " - foo" so the first paren the
            // parser sees is the year. The original title is preserved on result.Album.
            var titleForParser = ParenToDash.Replace(x.AlbumTitle, " - $1");
            titleForParser = CollapseSpaces.Replace(titleForParser, " ").Trim();
            result.Title = $"{x.ArtistName} - {titleForParser}";

            if (year > 0)
            {
                result.Title += $" ({year})";
            }

            if (explicitType == ExplicitStatus.Explicit)
            {
                result.Title += " [Explicit]";
            }
            else if (explicitType == ExplicitStatus.Clean)
            {
                result.Title += " [Clean]";
            }

            result.Title += $" [{format}] [WEB]";

            return result;
        }

        // based on the code for the /api/newReleases endpoint of deemix
        private async Task<DeezerSearchResponse> GenerateSearchResponseFromChannelData(string channelData)
        {
            var page = JObject.Parse(channelData)["results"]!;
            var musicSection = page["sections"]!.First(s => s["section_id"]!.ToString().Contains("module_id=83718b7b-5503-4062-b8b9-3530e2e2cefa"));
            var channels = musicSection["items"]!.Select(i => i["target"]!.ToString()).ToArray();

            var newReleasesByChannel = await Task.WhenAll(channels.Select(c => GetChannelNewReleases(c)));

            var seen = new ConcurrentDictionary<long, bool>();
            var distinct = new ConcurrentBag<JToken>();

            Parallel.ForEach(newReleasesByChannel.SelectMany(l => l), r =>
            {
                var id = r["ALB_ID"]!.Value<long>();
                if (seen.TryAdd(id, true))
                {
                    distinct.Add(r);
                }
            });

            var sortedDistinct = distinct.OrderByDescending(a => DateTime.TryParse(a["DIGITAL_RELEASE_DATE"]!.Value<string>()!, out var release) ? release : DateTime.MinValue).ToList();

            var now = DateTime.Now;
            var recent = sortedDistinct.Where(a => DateTime.TryParse(a["DIGITAL_RELEASE_DATE"]!.Value<string>()!, out var release) && (now - release).Days < 8);

            JObject baseObj = new();
            JArray dataArray = new();

            long albumCount = 0;

            await Task.WhenAll(recent.Select(async album =>
            {
                var id = album["ALB_ID"]!.Value<long>();
                var result = await DeezerAPI.Instance.Client.GWApi.GetAlbumPage(id);

                var duration = result["SONGS"]!.Sum(track => track.Contains("DURATION") ? track["DURATION"]!.Value<long>() : 0L);
                var trackCount = result["SONGS"]!.Count();

                var data = result["DATA"]!;
                data["DURATION"] = duration;
                data["NUMBER_TRACK"] = trackCount;
                data["LINK"] = $"https://deezer.com/album/{id}";

                lock (dataArray)
                {
                    dataArray.Add(data);
                    albumCount++;
                }
            }));

            baseObj.Add("data", dataArray);
            baseObj.Add("total", albumCount);

            return baseObj.ToObject<DeezerSearchResponse>()
                ?? throw new InvalidOperationException("Deezer channel response failed to deserialize into DeezerSearchResponse");
        }

        private async Task<JToken[]> GetChannelNewReleases(string channelName)
        {
            var channelData = await DeezerAPI.Instance.Client.GWApi.GetPage(channelName);
            Regex regex = new("New.*releases");

            var newReleasesSection = (JObject)channelData["sections"]!.FirstOrDefault(s => regex.IsMatch(s["title"]!.ToString()))!;
            if (newReleasesSection == null)
                return Array.Empty<JToken>();

            if (newReleasesSection.ContainsKey("target"))
            {
                var showAll = await DeezerAPI.Instance.Client.GWApi.GetPage(newReleasesSection["target"]!.ToString());
                return showAll["sections"]!.First()!["items"]!.Select(i => i["data"]!).ToArray();
            }

            return newReleasesSection.ContainsKey("items")
                ? newReleasesSection["items"]!.Select(i => i["data"]!).ToArray()
                : Array.Empty<JToken>();
        }
    }
}
