using NzbDrone.Common.Http;

namespace NzbDrone.Core.Indexers.Qobuz
{
    /// <summary>Shared by every request of one search; the parser sets MatchFound so gated fallback queries can skip.</summary>
    public sealed class QobuzSearchContext
    {
        public string? ArtistCleanName { get; init; }

        // Normalised core of the wanted album title; null for an artist search.
        public string? CoreTitle { get; init; }

        public bool MatchFound { get; set; }
    }

    /// <summary>Carries the search context through HttpIndexerBase.FetchPage to the parser as IndexerResponse.Request.</summary>
    public sealed class QobuzIndexerRequest(string url, QobuzSearchContext? context) : IndexerRequest(url, HttpAccept.Json)
    {
        public QobuzSearchContext? Context { get; } = context;
    }
}
