using NLog;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;

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
            return AlbumYearGuard.Apply(releases, criteria, indexerName, logger);
        }
    }
}
