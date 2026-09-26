using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Core.Model;
using NzbDrone.Plugin.Sleezer.Core.Utilities;

namespace NzbDrone.Plugin.Sleezer.Core.DecisionEngine
{
    /// <summary>Rejects a store copy offered as an upgrade whose tracks are not recordings of the album, such as a radio edit or a piano version.</summary>
    public class HeldReleaseSpecification(IReleaseService releaseService, ITrackService trackService, Lazy<IIndexerFactory> indexerFactory) : IDecisionEngineSpecification
    {
        public SpecificationPriority Priority => SpecificationPriority.Default;

        // Permanent, as in StoreMatchSpecification: a Temporary rejection parks the release in pending.
        public RejectionType Type => RejectionType.Permanent;

        public Decision IsSatisfiedBy(RemoteAlbum subject, SearchCriteriaBase searchCriteria)
        {
            if (subject?.Release is not StoreReleaseInfo release || subject.Albums is not [Album target])
                return Decision.Accept();

            // Lazy, as in SlskdIndexer: the indexer factory is resolved alongside the decision engine.
            if (!HeldReleaseCheck.StrictMatching(indexerFactory.Value.Find(release.IndexerId)?.Settings))
                return Decision.Accept();

            List<int> releaseIds = releaseService.GetReleasesByAlbum(target.Id).Select(r => r.Id).ToList();
            if (releaseIds.Count == 0)
                return Decision.Accept();

            return HeldReleaseCheck.Reason(release, trackService.GetTracksByReleases(releaseIds)) is { } reason
                ? Decision.Reject(reason)
                : Decision.Accept();
        }
    }
}
