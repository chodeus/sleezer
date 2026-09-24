using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Core.Model;
using NzbDrone.Plugin.Sleezer.Core.Utilities;

namespace NzbDrone.Plugin.Sleezer.Core.DecisionEngine
{
    /// <summary>Rejects a store copy dated like the artist's other album of the same title, such as the original of a guest version.</summary>
    public class SiblingAlbumSpecification(IAlbumService albumService, IReleaseService releaseService) : IDecisionEngineSpecification
    {
        public SpecificationPriority Priority => SpecificationPriority.Default;

        // Permanent, as in StoreMatchSpecification: a Temporary rejection parks the release in pending.
        public RejectionType Type => RejectionType.Permanent;

        public Decision IsSatisfiedBy(RemoteAlbum subject, SearchCriteriaBase searchCriteria)
        {
            if (subject?.Release is not StoreReleaseInfo release || subject.Albums is not [Album target] || subject.Artist == null)
                return Decision.Accept();

            Album? sibling = SiblingAlbumMatch.DatedSibling(release, target, albumService.GetAlbumsByArtist(subject.Artist.Id), releaseService.GetReleasesByAlbum);
            return sibling == null
                ? Decision.Accept()
                : Decision.Reject($"dated {release.PublishDate:yyyy-MM-dd}, like the artist's {sibling.ReleaseDate:yyyy-MM-dd} '{sibling.Title}' rather than the searched {target.ReleaseDate:yyyy-MM-dd} one");
        }
    }
}
