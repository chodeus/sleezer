using NLog;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Core.Model;

namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    /// <summary>The post-fetch checks for a store album search, in the one order that works.</summary>
    public static class StoreResultRefiner
    {
        // Guard before verification: a result retitled to the searched artist would otherwise keep the
        // artist-mismatch Rejection the verifier set, and StoreMatchSpecification would drop it.
        public static IList<ReleaseInfo> Refine(
            IList<ReleaseInfo> releases,
            AlbumSearchCriteria criteria,
            bool strictMatching,
            HashSet<string> duplicates,
            string indexerName,
            Logger logger)
        {
            if (releases.Count != 0)
                releases = AmbiguousArtistGuard.Apply(releases, criteria.Artist, duplicates, indexerName, logger);

            if (!strictMatching)
                return releases;

            releases = StoreReleaseVerifier.Apply(releases, criteria, indexerName, logger);
            releases = AlbumYearGuard.Apply(releases, criteria, indexerName, logger);
            return PresentAsSearched(releases, criteria, indexerName, logger);
        }

        // Lidarr maps a result by its title and rejects one whose clean title differs from the searched
        // album's, so a result verified as that album carries its title. Last, so every check has run.
        private static IList<ReleaseInfo> PresentAsSearched(IList<ReleaseInfo> releases, AlbumSearchCriteria criteria, string indexerName, Logger logger)
        {
            string? searched = criteria.Albums?.FirstOrDefault()?.Title ?? criteria.AlbumTitle;
            if (string.IsNullOrWhiteSpace(searched) || criteria.Artist is not { Name: { Length: > 0 } artist } searchedArtist)
                return releases;

            int retitled = 0;
            foreach (ReleaseInfo release in releases)
            {
                // Only an exact title and an exact credit: renamed, the result skips Lidarr's own title
                // and artist checks, and an unjudgeable title was passed unchecked, not matched.
                if (release is StoreReleaseInfo { Rejection: null } store
                    && StoreReleaseVerifier.CreditsArtist(store, searchedArtist)
                    && StoreReleaseVerifier.SameAlbumTitle(store.CandidateTitle ?? store.Album, searched)
                    && ReleaseTitle.AsSearchedAlbum(store, artist, searched))
                    retitled++;
            }

            if (retitled > 0)
                logger.Debug("{Indexer}: {Count} verified result(s) presented as '{Album}'", indexerName, retitled, searched);

            return releases;
        }
    }
}
