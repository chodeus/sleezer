using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Core.Model;
using NzbDrone.Plugin.Sleezer.Core.Utilities;

namespace NzbDrone.Plugin.Sleezer.Core.DecisionEngine
{
    /// <summary>Rejects a store copy that could not replace the files an album already has, such as a single offered as an upgrade.</summary>
    public class HeldReleaseSpecification(IReleaseService releaseService, ITrackService trackService) : IDecisionEngineSpecification
    {
        public SpecificationPriority Priority => SpecificationPriority.Default;

        // Permanent, as in StoreMatchSpecification: a Temporary rejection parks the release in pending.
        public RejectionType Type => RejectionType.Permanent;

        public Decision IsSatisfiedBy(RemoteAlbum subject, SearchCriteriaBase searchCriteria)
        {
            if (subject?.Release is not StoreReleaseInfo release || subject.Albums is not [Album target])
                return Decision.Accept();

            // Track files hang off the monitored release's tracks, the same one MoreTracksSpecification reads.
            AlbumRelease? held = releaseService.GetReleasesByAlbum(target.Id).FirstOrDefault(r => r.Monitored);
            if (held == null)
                return Decision.Accept();

            return HeldReleaseCheck.Reason(release, trackService.GetTracksByRelease(held.Id)) is { } reason
                ? Decision.Reject(reason)
                : Decision.Accept();
        }
    }
}
