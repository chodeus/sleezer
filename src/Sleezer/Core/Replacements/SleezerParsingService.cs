using NLog;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Plugin.Sleezer.Core.Replacements
{
    // ArtistRepository throws when two artists share a CleanName, and the throw jumps over the
    // history fallbacks in TrackedDownloadService.TrackDownload and CompletedDownloadService.Check.
    public class SleezerParsingService : ParsingService, IParsingService
    {
        private readonly Logger _logger;

        public SleezerParsingService(ITrackService trackService,
            IArtistService artistService,
            IAlbumService albumService,
            IMediaFileService mediaFileService,
            Logger logger)
            : base(trackService, artistService, albumService, mediaFileService, logger)
        {
            _logger = logger;
        }

        public new Artist? GetArtist(string title)
        {
            try
            {
                return base.GetArtist(title);
            }
            catch (MultipleArtistsFoundException e)
            {
                LogCollision(title, e);
                return null;
            }
        }

        // A null Artist is what Map already returns for an unknown one, so every caller handles it.
        public new RemoteAlbum Map(ParsedAlbumInfo parsedAlbumInfo, SearchCriteriaBase? searchCriteria = null)
        {
            try
            {
                return base.Map(parsedAlbumInfo, searchCriteria);
            }
            catch (MultipleArtistsFoundException e)
            {
                LogCollision(parsedAlbumInfo.ArtistName, e);
                return new RemoteAlbum { ParsedAlbumInfo = parsedAlbumInfo };
            }
        }

        // Unreferenced in Lidarr today, but it reaches the same FindByName: guard every name
        // lookup on this service, not only the two that are wired up.
        public new Artist? GetArtistFromTag(string file)
        {
            try
            {
                return base.GetArtistFromTag(file);
            }
            catch (MultipleArtistsFoundException e)
            {
                LogCollision(file, e);
                return null;
            }
        }

        // Info, not Debug: this replaces a queue warning the user could previously see without
        // turning Debug logging on. The exception already names the matching artists.
        private void LogCollision(string title, MultipleArtistsFoundException e) =>
            _logger.Info(e, "Multiple artists share a clean name for '{0}'; falling back to download history", title);
    }
}
