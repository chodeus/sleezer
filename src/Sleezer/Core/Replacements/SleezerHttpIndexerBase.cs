using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;
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
        protected SleezerHttpIndexerBase(
            IHttpClient httpClient,
            IIndexerStatusService indexerStatusService,
            IConfigService configService,
            IParsingService parsingService,
            Logger logger)
            : base(httpClient, indexerStatusService, configService, parsingService, logger)
        {
        }

        /// <summary>Indexer-specific filtering, applied before the shared guards and the count.</summary>
        protected virtual IList<ReleaseInfo> FilterReleases(IList<ReleaseInfo> releases, AlbumSearchCriteria searchCriteria) => releases;

        public override async Task<IList<ReleaseInfo>> Fetch(AlbumSearchCriteria searchCriteria)
        {
            // Subclass filter first, so the count below is what the caller actually receives.
            IList<ReleaseInfo> releases = FilterReleases(await base.Fetch(searchCriteria), searchCriteria);

            if (Settings is not IStoreMatchingSettings { StrictMatching: false })
            {
                releases = StoreReleaseVerifier.Apply(releases, searchCriteria, Name, _logger);
                releases = AlbumYearGuard.Apply(releases, searchCriteria, Name, _logger);
            }

            // Slskd accounts for its own searches; this is the same answer for the rest.
            _logger.Info("{Indexer}: {Count} result(s) for '{Artist} - {Album}'",
                Name, releases.Count, searchCriteria.Artist?.Name, searchCriteria.AlbumTitle);

            return releases;
        }
    }
}
