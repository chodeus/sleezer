using NzbDrone.Core.Download.Clients.Deezer;

namespace NzbDrone.Plugin.Sleezer.Core.Deezer
{
    /// <summary>Decides what a Deezer search response means, before any enrichment work.</summary>
    public static class DeezerSearchResponseReader
    {
        public enum Outcome
        {
            Results,
            Empty,
        }

        /// <summary>Throws when Deezer reported a failure; otherwise says whether anything came back.</summary>
        public static Outcome Read(DeezerSearchResponseWrapper? wrapper, string bodySnippet)
        {
            // Deezer's own error is more specific than the ARL guess below, so it is read first.
            if (wrapper?.Error?.HasValues == true)
            {
                throw Failure($"Deezer rejected the search request: {wrapper.Error.ToString(Newtonsoft.Json.Formatting.None)}", bodySnippet);
            }

            // results.data missing entirely is the shape a "null" api_token produces, which means
            // the ARL never opened a session.
            if (wrapper?.Results?.Data == null)
            {
                throw Failure(
                    "Deezer rejected the search request — ARL is missing or invalid. Re-authenticate at deezer.com, copy a fresh `arl` cookie, and restart Lidarr.",
                    bodySnippet);
            }

            return wrapper.Results.Data.Count == 0 ? Outcome.Empty : Outcome.Results;
        }

        private static InvalidOperationException Failure(string message, string bodySnippet)
        {
            var ex = new InvalidOperationException(message);
            ex.Data["DeezerResponseSnippet"] = bodySnippet;
            return ex;
        }
    }
}
