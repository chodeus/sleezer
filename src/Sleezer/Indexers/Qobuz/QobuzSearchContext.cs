using NzbDrone.Common.Http;
using NzbDrone.Plugin.Sleezer.Qobuz;

namespace NzbDrone.Core.Indexers.Qobuz
{
    /// <summary>Shared by every request of one search; the parser sets MatchFound so gated fallback queries can skip.</summary>
    public sealed class QobuzSearchContext
    {
        public string? ArtistCleanName { get; init; }

        // Normalised title of the wanted album, edition included; null for an artist search.
        public string? MatchTitle { get; init; }

        public bool MatchFound { get; set; }
    }

    /// <summary>Carries the search context through HttpIndexerBase.FetchPage to the parser as IndexerResponse.Request.</summary>
    public sealed class QobuzIndexerRequest(string url, QobuzSearchContext? context, QobuzAPI session)
        : SessionIndexerRequest<QobuzAPI>(url, HttpAccept.Json, session)
    {
        public QobuzSearchContext? Context { get; } = context;
    }
}
