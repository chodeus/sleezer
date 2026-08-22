using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Qobuz;
using QobuzApiSharp.Models.Content;

namespace NzbDrone.Core.Indexers.Qobuz
{
    public class QobuzParser : IParseIndexerResponse
    {
        // Album detail is only fetched for the head of the result list — enough to label
        // the releases a user will actually pick from, without an N+1 over 100 albums.
        private const int MaxDetailLookups = 10;
        private const int DetailConcurrency = 2;

        // ParseResponse is synchronous, so these lookups block the search thread. Bound
        // the whole batch: a stalled Qobuz would otherwise hold it indefinitely, and a
        // release type is only a title decoration.
        private static readonly TimeSpan DetailLookupBudget = TimeSpan.FromSeconds(20);

        // FLAC on real music lands around 60-70% of raw PCM. Only used for the size
        // estimate Lidarr shows; the true size isn't known until the file lands.
        private const double FlacCompressionFactor = 0.7;

        private static readonly Regex LocalePrefix = new(@"(qobuz\.com/)[a-z]{2}-[a-z]{2}/", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public QobuzIndexerSettings Settings { get; set; } = null!;
        public Logger Logger { get; set; } = null!;

        public IList<ReleaseInfo> ParseResponse(IndexerResponse response)
        {
            var content = new HttpResponse<SearchResult>(response.HttpResponse).Content;
            var jsonResponse = JObject.Parse(content).ToObject<SearchResult>();

            List<Album> albums = jsonResponse?.Albums?.Items?.ToList() ?? [];
            if (albums.Count == 0)
                return [];

            if (Settings.HideNonStreamable)
            {
                int before = albums.Count;
                albums = [.. albums.Where(a => a.Streamable ?? true)];
                if (albums.Count != before)
                    Logger.Debug("Qobuz hid {Count} non-streamable album(s) — not licensed for account country {Country}",
                        before - albums.Count, QobuzAPI.Instance?.CountryCode);
            }

            Dictionary<string, string> releaseTypes = ResolveReleaseTypes(albums);

            return
            [
                .. albums
                    .SelectMany(album => ProcessAlbumResult(album, releaseTypes))
                    .OrderBy(QualityPriority)
                    .ThenBy(r => r.Size)
            ];
        }

        private static int QualityPriority(ReleaseInfo r) => r.Container switch
        {
            "Lossless" => 0,
            "24bit 96kHz" => 1,
            "24bit 192kHz" => 2,
            _ => 3
        };

        // Qobuz populates release_type on /album/get but not always on /album/search.
        // Take it from the search payload when it's there and only pay for a detail call
        // on the head of the list when it isn't.
        private Dictionary<string, string> ResolveReleaseTypes(List<Album> albums)
        {
            Dictionary<string, string> result = new(StringComparer.Ordinal);

            foreach (Album album in albums.Where(a => !string.IsNullOrEmpty(a.ReleaseType)))
                result[album.Id] = album.ReleaseType;

            List<Album> missing = [.. albums.Where(a => !result.ContainsKey(a.Id)).Take(MaxDetailLookups)];
            if (missing.Count == 0)
                return result;

            using SemaphoreSlim gate = new(DetailConcurrency, DetailConcurrency);
            using CancellationTokenSource budget = new(DetailLookupBudget);
            var lookups = missing.Select(async album =>
            {
                await gate.WaitAsync(budget.Token);
                try
                {
                    return (album.Id, Type: await Task.Run(() => QobuzAPI.Instance?.Client?.GetAlbum(album.Id, true)?.ReleaseType, budget.Token));
                }
                catch (Exception ex)
                {
                    Logger.Debug(ex, "Qobuz album detail lookup failed for {AlbumId}; release type omitted from the title", album.Id);
                    return (album.Id, Type: null);
                }
                finally
                {
                    gate.Release();
                }
            }).ToArray();

            try
            {
                foreach (var (id, type) in Task.WhenAll(lookups).GetAwaiter().GetResult())
                {
                    if (!string.IsNullOrEmpty(type))
                        result[id] = type;
                }
            }
            catch (OperationCanceledException)
            {
                Logger.Debug("Qobuz album detail lookups exceeded {Budget}s; releases keep the type the search payload carried", DetailLookupBudget.TotalSeconds);
            }

            return result;
        }

        private static IEnumerable<ReleaseInfo> ProcessAlbumResult(Album result, Dictionary<string, string> releaseTypes)
        {
            List<AudioQuality> qualityList = [AudioQuality.MP3320, AudioQuality.FLACLossless];

            if ((result.Hires ?? false) && (result.HiresStreamable ?? false))
            {
                qualityList.Add(AudioQuality.FLACHiRes24Bit96kHz);
                if ((result.MaximumSamplingRate ?? 0) > 96)
                    qualityList.Add(AudioQuality.FLACHiRes24Bit192Khz);
            }

            releaseTypes.TryGetValue(result.Id, out string? releaseType);
            return qualityList.Select(q => ToReleaseInfo(result, q, releaseType));
        }

        // Qobuz returns album URLs on its default "fr-fr" storefront; rewrite to the
        // signed-in account's locale so Lidarr's info link opens the right one. The
        // download only needs the album ID out of this URL, so this is cosmetic.
        private static string LocalizeUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
                return url;

            var user = QobuzAPI.Instance?.Login?.User;
            if (string.IsNullOrEmpty(user?.CountryCode) || string.IsNullOrEmpty(user?.LanguageCode))
                return url;

            return LocalePrefix.Replace(url, $"$1{user.CountryCode}-{user.LanguageCode}".ToLowerInvariant() + "/");
        }

        private static ReleaseInfo ToReleaseInfo(Album x, AudioQuality bitrate, string? releaseType)
        {
            var publishDate = DateTime.UtcNow;
            var year = 0;
            if (x.ReleaseDateOriginal != null)
            {
                publishDate = x.ReleaseDateOriginal.Value.DateTime;
                year = publishDate.Year;
            }

            var url = LocalizeUrl(x.Url);

            var result = new ReleaseInfo
            {
                Guid = $"Qobuz-{x.Id}-{bitrate}",
                Artist = x.Artist?.Name,
                Album = x.CompleteTitle,
                DownloadUrl = url,
                InfoUrl = url,
                PublishDate = publishDate,
                DownloadProtocol = nameof(QobuzDownloadProtocol)
            };

            string format;
            switch (bitrate)
            {
                case AudioQuality.MP3320:
                    result.Codec = "MP3";
                    result.Container = "320";
                    format = "MP3 320kbps";
                    break;
                case AudioQuality.FLACLossless:
                    result.Codec = "FLAC";
                    result.Container = "Lossless";
                    format = "FLAC Lossless";
                    break;
                case AudioQuality.FLACHiRes24Bit96kHz:
                    result.Codec = "FLAC";
                    result.Container = "24bit 96kHz";
                    format = "FLAC 24bit 96kHz";
                    break;
                case AudioQuality.FLACHiRes24Bit192Khz:
                    result.Codec = "FLAC";
                    result.Container = "24bit 192kHz";
                    format = "FLAC 24bit 192kHz";
                    break;
                default:
                    throw new NotSupportedException($"Unhandled Qobuz audio quality {bitrate}");
            }

            result.Size = EstimateSize(x, bitrate);
            result.Title = $"{x.Artist?.Name} - {x.CompleteTitle}";

            if (year > 0)
                result.Title += $" ({year})";

            if (!string.IsNullOrEmpty(releaseType))
                result.Title += $" [{releaseType}]";

            if (x.ParentalWarning.GetValueOrDefault())
                result.Title += " [Explicit]";

            result.Title += $" [{format}] [WEB]";

            return result;
        }

        // Raw PCM bitrate / 8 for bytes, with a FLAC compression factor. Lossless is
        // always CD quality; Hi-Res uses the album's own specs capped at the tier max.
        private static long EstimateSize(Album x, AudioQuality bitrate)
        {
            double bitsPerSecond = bitrate switch
            {
                AudioQuality.MP3320 => 320_000,
                AudioQuality.FLACLossless => 16.0 * 44_100 * 2,
                AudioQuality.FLACHiRes24Bit96kHz =>
                    (x.MaximumBitDepth ?? 24) * (Math.Min(x.MaximumSamplingRate ?? 96, 96) * 1000) * (x.MaximumChannelCount ?? 2),
                AudioQuality.FLACHiRes24Bit192Khz =>
                    (x.MaximumBitDepth ?? 24) * ((x.MaximumSamplingRate ?? 192) * 1000) * (x.MaximumChannelCount ?? 2),
                _ => 320_000
            };

            double compressionFactor = bitrate == AudioQuality.MP3320 ? 1.0 : FlacCompressionFactor;
            return (long)((x.Duration ?? 0) * bitsPerSecond / 8 * compressionFactor);
        }
    }
}
