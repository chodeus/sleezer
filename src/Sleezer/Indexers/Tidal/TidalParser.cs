using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Core.Tidal;
using NzbDrone.Plugin.Sleezer.Tidal;
using TidalSharp.Data;
using TidalSharp.Exceptions;

namespace NzbDrone.Core.Indexers.Tidal
{
    public class TidalParser : IParseIndexerResponse
    {
        public TidalIndexerSettings Settings { get; set; } = null!;
        public Logger? Logger { get; set; }

        public IList<ReleaseInfo> ParseResponse(IndexerResponse response)
        {
            var content = new HttpResponse<TidalSearchResponse>(response.HttpResponse).Content;
            var jsonResponse = JObject.Parse(content).ToObject<TidalSearchResponse>();
            if (jsonResponse?.AlbumResults?.Items == null)
                return Array.Empty<ReleaseInfo>();

            var releases = new List<ReleaseInfo>();
            foreach (var album in jsonResponse.AlbumResults.Items)
                releases.AddRange(ProcessAlbumResult(album));

            // Resolve track-only hits to their full album so the result set
            // covers albums Tidal didn't return as a top-level album hit.
            // Run async lookups in parallel rather than the sync-over-async
            // .Wait() loop the upstream plugin uses.
            if (jsonResponse.TrackResults?.Items != null)
            {
                var alreadyHave = new HashSet<string>(jsonResponse.AlbumResults.Items.Select(a => a.Id));
                var trackTasks = jsonResponse.TrackResults.Items
                    .Where(t => t.Album != null && !alreadyHave.Contains(t.Album.Id))
                    .Select(ProcessTrackAlbumResultAsync)
                    .ToArray();

                var resolved = Task.WhenAll(trackTasks).GetAwaiter().GetResult();
                foreach (var batch in resolved)
                    if (batch != null)
                        releases.AddRange(batch);
            }

            return releases.OrderByDescending(o => o.Size).ToArray();
        }

        public static bool ArtistMatchesAlbum(string searchArtistQuery, IEnumerable<string> albumArtistNames)
            => TidalArtistMatcher.ArtistMatches(searchArtistQuery, albumArtistNames);

        private IEnumerable<ReleaseInfo> ProcessAlbumResult(TidalSearchResponse.Album result)
        {
            var qualityList = new List<AudioQuality> { AudioQuality.LOW, AudioQuality.HIGH };

            if (result.MediaMetadata?.Tags != null)
            {
                if (result.MediaMetadata.Tags.Contains("HIRES_LOSSLESS"))
                {
                    qualityList.Add(AudioQuality.LOSSLESS);
                    qualityList.Add(AudioQuality.HI_RES_LOSSLESS);
                }
                else if (result.MediaMetadata.Tags.Contains("LOSSLESS"))
                {
                    qualityList.Add(AudioQuality.LOSSLESS);
                }
            }

            return qualityList.Select(q => ToReleaseInfo(result, q));
        }

        private async Task<IEnumerable<ReleaseInfo>?> ProcessTrackAlbumResultAsync(TidalSearchResponse.Track result)
        {
            try
            {
                var instance = TidalAPI.Instance;
                if (instance == null)
                    return null;

                var album = (await instance.Client.API.GetAlbum(result.Album.Id))
                    .ToObject<TidalSearchResponse.Album>();
                return album == null ? null : ProcessAlbumResult(album);
            }
            catch (ResourceNotFoundException ex)
            {
                Logger?.Debug(ex, "Tidal album {AlbumId} not found while resolving track {TrackId}", result.Album?.Id, result.Id);
                return null;
            }
            catch (Exception ex)
            {
                // One bad track-lookup must not tank the whole search response.
                // Task.WhenAll surfaces the first inner exception, so swallow
                // and log here; the parent ParseResponse keeps the album-tier
                // results plus any other track resolutions that succeeded.
                Logger?.Debug(ex, "Tidal album lookup failed for track {TrackId}; skipping", result.Id);
                return null;
            }
        }

        private static ReleaseInfo ToReleaseInfo(TidalSearchResponse.Album x, AudioQuality bitrate)
        {
            var publishDate = DateTime.UtcNow;
            var year = 0;
            if (DateTime.TryParse(x.ReleaseDate, out var digitalReleaseDate))
            {
                publishDate = digitalReleaseDate;
                year = publishDate.Year;
            }
            else if (DateTime.TryParse(x.StreamStartDate, out var startStreamDate))
            {
                publishDate = startStreamDate;
                year = startStreamDate.Year;
            }

            var url = x.Url;
            var result = new ReleaseInfo
            {
                Guid = $"Tidal-{x.Id}-{bitrate}",
                Artist = x.Artists.First().Name,
                Album = x.Title,
                DownloadUrl = url,
                InfoUrl = url,
                PublishDate = publishDate,
                DownloadProtocol = nameof(TidalDownloadProtocol)
            };

            string format;
            switch (bitrate)
            {
                case AudioQuality.LOW:
                    result.Codec = "AAC";
                    result.Container = "96";
                    format = "AAC (M4A) 96kbps";
                    break;
                case AudioQuality.HIGH:
                    result.Codec = "AAC";
                    result.Container = "320";
                    format = "AAC (M4A) 320kbps";
                    break;
                case AudioQuality.LOSSLESS:
                    result.Codec = "FLAC";
                    result.Container = "Lossless";
                    format = "FLAC (M4A) Lossless";
                    break;
                case AudioQuality.HI_RES_LOSSLESS:
                    result.Codec = "FLAC";
                    result.Container = "24bit Lossless";
                    format = "FLAC (M4A) 24bit Lossless";
                    break;
                default:
                    throw new NotImplementedException();
            }

            // Estimate; Tidal's API does not expose exact file sizes.
            var bps = bitrate switch
            {
                AudioQuality.HI_RES_LOSSLESS => 1152000,
                AudioQuality.LOSSLESS => 176400,
                AudioQuality.HIGH => 40000,
                AudioQuality.LOW => 12000,
                _ => 40000
            };
            result.Size = x.Duration * bps;
            result.Title = $"{x.Artists.First().Name} - {x.Title}";

            if (year > 0)
                result.Title += $" ({year})";
            if (x.Explicit)
                result.Title += " [Explicit]";

            result.Title += $" [{format}] [WEB]";
            return result;
        }
    }
}
