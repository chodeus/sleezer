using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeezNET.Data;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Plugin.Sleezer.Core.Deezer;
using NzbDrone.Plugin.Sleezer.Deezer;

namespace NzbDrone.Core.Download.Clients.Deezer
{
    /// <summary>Resolves Deezer's own substitute for a dead track — see DeezerFallbackGate for what qualifies.</summary>
    public static class DeezerTrackFallback
    {
        private const int MaxAlternativeAlbums = 3;

        // Route 1: DATA.FALLBACK, Deezer's supersession pointer. Route 2: the page's ISRC
        // section, Deezer's own list of other releases carrying the identical recording.
        public static async Task<JToken?> TryResolveAsync(long originalId, JToken originalPage, Bitrate bitrate, Logger logger, CancellationToken ct)
        {
            var original = ToCandidate(originalPage["DATA"]!);

            var fallbackId = originalPage["DATA"]?["FALLBACK"]?["SNG_ID"]?.Value<long>() ?? 0;
            if (fallbackId > 0 && fallbackId != originalId)
            {
                var page = await TryCandidateAsync(fallbackId, original, bitrate, "FALLBACK pointer", logger, ct);
                if (page != null)
                    return page;
            }

            var originalIsrc = originalPage["DATA"]?["ISRC"]?.ToString();
            if (string.IsNullOrWhiteSpace(originalIsrc))
                return null;

            var alternativeAlbums = originalPage["ISRC"]?["data"] as JArray;
            if (alternativeAlbums == null)
                return null;

            var probed = 0;
            foreach (var album in alternativeAlbums)
            {
                if (probed >= MaxAlternativeAlbums)
                    break;

                var rights = album["RIGHTS"];
                var streamable = rights?["STREAM_SUB_AVAILABLE"]?.ToObject<bool?>() == true
                    || rights?["STREAM_ADS_AVAILABLE"]?.ToObject<bool?>() == true;
                var albumId = album["ALB_ID"]?.Value<long>() ?? 0;
                if (!streamable || albumId <= 0)
                    continue;

                probed++;
                JToken albumPage;
                try
                {
                    albumPage = await DeezerAPI.Instance.Client.GWApi.GetAlbumPage(albumId, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.Debug(ex, "Deezer fallback: could not load alternative album {AlbumId} for track {TrackId}", albumId, originalId);
                    continue;
                }

                var candidateId = (albumPage["SONGS"]?["data"] as JArray)?
                    .Where(t => string.Equals(t["ISRC"]?.ToString(), originalIsrc, StringComparison.OrdinalIgnoreCase))
                    .Select(t => t["SNG_ID"]?.Value<long>() ?? 0)
                    .FirstOrDefault(id => id > 0 && id != originalId && id != fallbackId) ?? 0;
                if (candidateId == 0)
                    continue;

                var page = await TryCandidateAsync(candidateId, original, bitrate, $"same-ISRC album {albumId}", logger, ct);
                if (page != null)
                    return page;
            }

            return null;
        }

        internal static string FilesizeKey(Bitrate bitrate) => bitrate switch
        {
            Bitrate.MP3_128 => "FILESIZE_MP3_128",
            Bitrate.MP3_320 => "FILESIZE_MP3_320",
            Bitrate.FLAC => "FILESIZE_FLAC",
            _ => "FILESIZE"
        };

        internal static long SizeFor(JToken data, Bitrate bitrate) =>
            long.TryParse(data[FilesizeKey(bitrate)]?.ToString(), out var size) ? size : 0;

        private static async Task<JToken?> TryCandidateAsync(long candidateId, FallbackCandidate original, Bitrate bitrate, string route, Logger logger, CancellationToken ct)
        {
            JToken page;
            try
            {
                page = await DeezerAPI.Instance.Client.GWApi.GetTrackPage(candidateId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.Debug(ex, "Deezer fallback: could not load candidate track {CandidateId} via {Route}", candidateId, route);
                return null;
            }

            var data = page["DATA"]!;
            if (!DeezerFallbackGate.Accept(original, ToCandidate(data), out var reason))
            {
                logger.Info("Deezer fallback: rejected candidate {CandidateId} via {Route} — {Reason}", candidateId, route, reason);
                return null;
            }

            if (SizeFor(data, bitrate) <= 0)
            {
                logger.Info("Deezer fallback: candidate {CandidateId} via {Route} has no {Bitrate} file", candidateId, route, bitrate);
                return null;
            }

            logger.Info("Deezer fallback: accepted candidate {CandidateId} via {Route} — {Reason}", candidateId, route, reason);
            return page;
        }

        private static FallbackCandidate ToCandidate(JToken data) => new(
            data["ISRC"]?.ToString(),
            data["SNG_TITLE"]?.ToString(),
            data["VERSION"]?.ToString(),
            data["DURATION"]?.Value<int>() ?? 0,
            data["EXPLICIT_LYRICS"]?.ToString() == "1");
    }
}
