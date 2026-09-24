using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Core.Model;
using NzbDrone.Plugin.Sleezer.Core.Utilities;

namespace NzbDrone.Plugin.Sleezer.Core.Replacements
{
    /// <summary>Lidarr's HTTP indexer plus the post-filters every Sleezer indexer wants.</summary>
    public abstract class SleezerHttpIndexerBase<TSettings> : HttpIndexerBase<TSettings>
        where TSettings : IIndexerSettings, new()
    {
        private readonly IArtistService _artistService;

        protected SleezerHttpIndexerBase(
            IHttpClient httpClient,
            IIndexerStatusService indexerStatusService,
            IConfigService configService,
            IParsingService parsingService,
            IArtistService artistService,
            Logger logger)
            : base(httpClient, indexerStatusService, configService, parsingService, logger)
        {
            _artistService = artistService;
        }

        /// <summary>Indexer-specific filtering, applied before the shared guards and the count.</summary>
        protected virtual IList<ReleaseInfo> FilterReleases(IList<ReleaseInfo> releases, AlbumSearchCriteria searchCriteria) => releases;

        public override async Task<IList<ReleaseInfo>> Fetch(AlbumSearchCriteria searchCriteria)
        {
            // Subclass filter first, so the count below is what the caller actually receives.
            // One duplicate set per search, so the tier check and the guard below judge the same snapshot.
            IList<ReleaseInfo> releases;
            HashSet<string> duplicates;
            using (AmbiguousArtistScope scope = AmbiguousArtistScope.Begin(searchCriteria.Artist, _artistService.GetAllArtists))
            {
                duplicates = scope.Duplicates;
                releases = FilterReleases(await base.Fetch(searchCriteria), searchCriteria);
            }

            releases = StoreResultRefiner.Refine(releases, searchCriteria, StrictMatching, duplicates, Name, _logger);

            // Slskd accounts for its own searches; this is the same answer for the rest.
            _logger.Info("{Indexer}: {Count} result(s) for '{Artist} - {Album}'",
                Name, releases.Count, searchCriteria.Artist?.Name, searchCriteria.AlbumTitle);

            return releases;
        }

        public override async Task<IList<ReleaseInfo>> Fetch(ArtistSearchCriteria searchCriteria)
        {
            using AmbiguousArtistScope scope = AmbiguousArtistScope.Begin(searchCriteria.Artist, _artistService.GetAllArtists);
            return AmbiguousArtistGuard.Apply(await base.Fetch(searchCriteria), searchCriteria.Artist, scope.Duplicates, Name, _logger);
        }

        // Lidarr stops at the first tier with any valid result; a tier holding only results the guard
        // will drop must read as empty, or the next tier never runs.
        protected override bool IsValidRelease(ReleaseInfo release)
        {
            if (!base.IsValidRelease(release))
                return false;

            if (!AmbiguousArtistScope.Rejects(release))
                return true;

            _logger.Debug("{Indexer}: dropping '{Title}' — its credited artist matches more than one library artist", Name, release.Title);
            return false;
        }

        // Indexers without the setting always verify.
        private bool StrictMatching => Settings is not IStoreMatchingSettings { StrictMatching: false };

        // For an override that never calls base.Fetch.
        protected IList<ReleaseInfo> RefineStoreResults(IList<ReleaseInfo> releases, AlbumSearchCriteria searchCriteria) =>
            StoreResultRefiner.Refine(releases, searchCriteria, StrictMatching, AmbiguousArtistGuard.DuplicatedCleanNames(_artistService.GetAllArtists()), Name, _logger);

        // After paging, so a drop here cannot end pagination early.
        protected IList<ReleaseInfo> GuardAmbiguousArtists(IList<ReleaseInfo> releases, Artist? searched) =>
            releases.Count == 0 ? releases : AmbiguousArtistGuard.Apply(releases, searched, _artistService.GetAllArtists(), Name, _logger);
    }
}
