using System.Collections.Concurrent;
using NLog;
using NzbDrone.Common.Http;

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

        // Successes only: a failed lookup must be retried next time, not remembered.
        private static readonly ConcurrentDictionary<string, IReadOnlyList<string>> Cache = new();

        public IReadOnlyList<string>? GuestsOf(string releaseGroupId, string primaryArtistId)
        {
            string key = $"{releaseGroupId}|{primaryArtistId}";
            if (Cache.TryGetValue(key, out IReadOnlyList<string>? cached))
                return cached;

            try
            {
                // MusicBrainz allows one request per second per client.
                HttpRequest request = new HttpRequestBuilder($"https://musicbrainz.org/ws/2/release-group/{releaseGroupId}")
                    .AddQueryParam("inc", "artist-credits")
                    .AddQueryParam("fmt", "json")
                    .WithRateLimit(1.1)
                    .Build();
                request.Headers.Add("User-Agent", UserAgent);
                request.RequestTimeout = TimeSpan.FromSeconds(10);

                IReadOnlyList<string> guests = GuestCredits.FromMusicBrainz(httpClient.Get(request).Content, primaryArtistId);
                Cache[key] = guests;
                return guests;
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "MusicBrainz credits unavailable for release group {ReleaseGroupId}", releaseGroupId);
                return null;
            }
        }
    }
}
