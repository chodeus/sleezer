using System.Collections.Concurrent;
using System.Net;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Plugin.Sleezer.Core.Model;

namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    public interface IMusicBrainzLookups
    {
        /// <summary>Names a release group credits besides the primary artist; null when MusicBrainz can't be read.</summary>
        IReadOnlyList<string>? GuestsOf(string releaseGroupId, string primaryArtistId);

        /// <summary>A recording's title and length; null when MusicBrainz can't be read or no longer has it.</summary>
        MusicBrainzRecording? RecordingOf(string recordingId);
    }

    /// <summary>Reads artist credits and recordings straight from MusicBrainz, which Lidarr's metadata drops or never holds.</summary>
    public class MusicBrainzLookups(IHttpClient httpClient, Logger logger) : IMusicBrainzLookups
    {
        private static readonly string UserAgent = $"Sleezer/{PluginInfo.Version} ( {PluginInfo.RepoUrl} )";

        // Successes only: a failed lookup must be retried later, not remembered.
        private static readonly ConcurrentDictionary<string, IReadOnlyList<string>?> Guests = new();
        private static readonly ConcurrentDictionary<string, MusicBrainzRecording?> Recordings = new();

        // These lookups run inside release decisions; one stalled request pauses its kind for five minutes
        // rather than letting every candidate wait out its own timeout. One breaker each: a missing guest list lets sibling copies through.
        internal static readonly ICircuitBreaker GuestsBreaker = CircuitBreakerFactory.GetCustomBreaker<MusicBrainzLookups>(1, 5);
        internal static readonly ICircuitBreaker RecordingsBreaker = new ApiCircuitBreaker(1, 5);

        // A release group MusicBrainz no longer has credits nobody; that is an answer, not an outage.
        public IReadOnlyList<string>? GuestsOf(string releaseGroupId, string primaryArtistId) =>
            Read(Guests, GuestsBreaker, $"{releaseGroupId}|{primaryArtistId}", $"release-group/{releaseGroupId}", "artist-credits",
                json => GuestCredits.FromMusicBrainz(json, primaryArtistId), notFound: []);

        public MusicBrainzRecording? RecordingOf(string recordingId) =>
            Read(Recordings, RecordingsBreaker, recordingId.ToLowerInvariant(), $"recording/{recordingId}", null, MusicBrainzRecording.FromJson, notFound: null);

        private T? Read<T>(ConcurrentDictionary<string, T?> cache, ICircuitBreaker breaker, string key, string resource, string? inc, Func<string, T> parse, T? notFound)
            where T : class
        {
            if (cache.TryGetValue(key, out T? cached))
                return cached;

            if (breaker.IsOpen)
                return null;

            try
            {
                // MusicBrainz allows one request per second per client.
                HttpRequestBuilder builder = new($"https://musicbrainz.org/ws/2/{resource}");
                if (inc != null)
                    builder.AddQueryParam("inc", inc);
                HttpRequest request = builder.AddQueryParam("fmt", "json").WithRateLimit(1.1).Build();
                request.Headers.Add("User-Agent", UserAgent);
                request.RequestTimeout = TimeSpan.FromSeconds(5);
                request.SuppressHttpError = true;

                HttpResponse response = httpClient.Get(request);
                T? value = response.StatusCode switch
                {
                    HttpStatusCode.OK => parse(response.Content),
                    HttpStatusCode.NotFound => notFound,
                    _ => throw new HttpException(request, response)
                };

                breaker.RecordSuccess();
                cache[key] = value;
                return value;
            }
            catch (Exception ex)
            {
                breaker.RecordFailure();
                logger.Debug(ex, "MusicBrainz unavailable for {Resource}; pausing lookups for five minutes", resource);
                return null;
            }
        }
    }
}
