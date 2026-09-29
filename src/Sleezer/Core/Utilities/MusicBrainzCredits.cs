using System.Collections.Concurrent;
using System.Net;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Plugin.Sleezer.Core.Model;

namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    public interface IMusicBrainzCredits
    {
        /// <summary>Names a release group credits besides the primary artist; null when MusicBrainz can't be read.</summary>
        IReadOnlyList<string>? GuestsOf(string releaseGroupId, string primaryArtistId);
    }

    /// <summary>Reads artist credits straight from MusicBrainz, which Lidarr's metadata drops to one artist per album.</summary>
    public class MusicBrainzCredits(IHttpClient httpClient, Logger logger) : IMusicBrainzCredits
    {
        private static readonly string UserAgent = $"Sleezer/{PluginInfo.Version} ( {PluginInfo.RepoUrl} )";

        // Successes only: a failed lookup must be retried later, not remembered.
        private static readonly ConcurrentDictionary<string, IReadOnlyList<string>> Cache = new();

        // These lookups run inside release decisions; one stalled request pauses them all for five minutes
        // rather than letting every candidate wait out its own timeout.
        private static readonly ICircuitBreaker Breaker = CircuitBreakerFactory.GetCustomBreaker<MusicBrainzCredits>(1, 5);

        public IReadOnlyList<string>? GuestsOf(string releaseGroupId, string primaryArtistId)
        {
            string key = $"{releaseGroupId}|{primaryArtistId}";
            if (Cache.TryGetValue(key, out IReadOnlyList<string>? cached))
                return cached;

            if (Breaker.IsOpen)
                return null;

            try
            {
                // MusicBrainz allows one request per second per client.
                HttpRequest request = new HttpRequestBuilder($"https://musicbrainz.org/ws/2/release-group/{releaseGroupId}")
                    .AddQueryParam("inc", "artist-credits")
                    .AddQueryParam("fmt", "json")
                    .WithRateLimit(1.1)
                    .Build();
                request.Headers.Add("User-Agent", UserAgent);
                request.RequestTimeout = TimeSpan.FromSeconds(5);
                request.SuppressHttpError = true;

                HttpResponse response = httpClient.Get(request);

                // A release group MusicBrainz no longer has credits nobody; that is an answer, not an outage.
                IReadOnlyList<string> guests = response.StatusCode switch
                {
                    HttpStatusCode.OK => GuestCredits.FromMusicBrainz(response.Content, primaryArtistId),
                    HttpStatusCode.NotFound => [],
                    _ => throw new HttpException(request, response)
                };

                Breaker.RecordSuccess();
                Cache[key] = guests;
                return guests;
            }
            catch (Exception ex)
            {
                Breaker.RecordFailure();
                logger.Debug(ex, "MusicBrainz credits unavailable for release group {ReleaseGroupId}; pausing lookups for five minutes", releaseGroupId);
                return null;
            }
        }
    }
}
