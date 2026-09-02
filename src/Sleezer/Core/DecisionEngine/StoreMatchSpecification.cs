using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Core.Model;

namespace NzbDrone.Plugin.Sleezer.Core.DecisionEngine
{
    /// <summary>Turns a verification verdict into a Lidarr rejection so the reason is visible.</summary>
    public class StoreMatchSpecification : IDecisionEngineSpecification
    {
        public SpecificationPriority Priority => SpecificationPriority.Default;

        // Permanent: automatic search must never grab these, and a Temporary rejection
        // would park them in the pending queue instead.
        public RejectionType Type => RejectionType.Permanent;

        // Keys on the type: only Sleezer's parsers implement IVerifiableRelease.
        public Decision IsSatisfiedBy(RemoteAlbum subject, SearchCriteriaBase searchCriteria)
        {
            return subject?.Release is IVerifiableRelease { Rejection: { Length: > 0 } reason }
                ? Decision.Reject(reason)
                : Decision.Accept();
        }
    }
}
