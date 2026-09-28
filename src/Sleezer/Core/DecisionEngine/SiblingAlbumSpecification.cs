using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Core.Model;
using NzbDrone.Plugin.Sleezer.Core.Utilities;

namespace NzbDrone.Plugin.Sleezer.Core.DecisionEngine
{
    /// <summary>Rejects a store copy that belongs to the artist's other album: titled as it, or dated like it under the same title.</summary>
    public class SiblingAlbumSpecification(IAlbumService albumService, IReleaseService releaseService) : IDecisionEngineSpecification
    {
        public SpecificationPriority Priority => SpecificationPriority.Default;

        // Permanent, as in StoreMatchSpecification: a Temporary rejection parks the release in pending.
        public RejectionType Type => RejectionType.Permanent;

        public Decision IsSatisfiedBy(RemoteAlbum subject, SearchCriteriaBase searchCriteria)
        {
            if (subject?.Release is not StoreReleaseInfo release || subject.Albums is not [Album target] || subject.Artist == null)
                return Decision.Accept();

            List<Album> artistAlbums = albumService.GetAlbumsByArtist(subject.Artist.Id);
            if (SiblingAlbumMatch.NamedSibling(release, target, artistAlbums) is { } named)
                return Decision.Reject($"titled '{named.Title}', the artist's other album, rather than the searched '{target.Title}'");

            // The dates DatedSibling compared, not the albums' own: a reissue date can be the one that matched.
            return SiblingAlbumMatch.DatedSibling(release, target, artistAlbums, releaseService.GetReleasesByAlbum) is not { } match
                ? Decision.Accept()
                : Decision.Reject($"dated {release.PublishDate:yyyy-MM-dd}, like the artist's {match.SiblingDate:yyyy-MM-dd} '{match.Sibling.Title}' rather than the searched {match.TargetDate:yyyy-MM-dd} one");
        }
    }
}
