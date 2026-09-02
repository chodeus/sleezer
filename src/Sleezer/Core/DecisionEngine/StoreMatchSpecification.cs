using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Core.Model;

namespace NzbDrone.Plugin.Sleezer.Core.DecisionEngine
{
    /// <summary>
    /// Turns a store result's verification verdict into a Lidarr rejection, so the reason
    /// shows on the interactive-search row and the operator can still grab it.
    /// </summary>
    public class StoreMatchSpecification : IDecisionEngineSpecification
    {
        public SpecificationPriority Priority => SpecificationPriority.Default;

        // Permanent: automatic search must never grab these, and a Temporary rejection
        // would park them in the pending queue instead.
        public RejectionType Type => RejectionType.Permanent;

        // Keys on the type, not the indexer name — only Sleezer's own parsers implement
        // IVerifiableRelease, so torrent and Usenet results can never match.
        public Decision IsSatisfiedBy(RemoteAlbum subject, SearchCriteriaBase searchCriteria)
        {
            return subject?.Release is IVerifiableRelease { Rejection: { Length: > 0 } reason }
                ? Decision.Reject(reason)
                : Decision.Accept();
        }
    }
}
