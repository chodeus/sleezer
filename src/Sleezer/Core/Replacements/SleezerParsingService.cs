using NLog;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Plugin.Sleezer.Core.Replacements
{
    // Returns null instead of throwing when two artists share a CleanName, so the history fallbacks
    // in TrackedDownloadService.TrackDownload and CompletedDownloadService.Check get to run.
    public class SleezerParsingService : IParsingService
    {
        // Composed, not inherited: develop's ParsingService constructor takes a parameter the pinned
        // submodule lacks, so a subclass would call a base constructor that does not exist at runtime.
        private readonly ParsingService _inner;
        private readonly Logger _logger;

        public SleezerParsingService(ParsingService inner, Logger logger)
        {
            _inner = inner;
            _logger = logger;
        }

        public Artist? GetArtist(string title)
        {
            try
            {
                return _inner.GetArtist(title);
            }
            catch (MultipleArtistsFoundException e)
            {
                LogCollision(title, e);
                return null;
            }
        }

        // Unreferenced in Lidarr today, but it reaches the same FindByName: guard every name
        // lookup on this service, not only the two that are wired up.
        public Artist? GetArtistFromTag(string file)
        {
            try
            {
                return _inner.GetArtistFromTag(file);
            }
            catch (MultipleArtistsFoundException e)
            {
                LogCollision(file, e);
                return null;
            }
        }

        // A null Artist is what Map already returns for an unknown one, so every caller handles it.
        public RemoteAlbum Map(ParsedAlbumInfo parsedAlbumInfo, SearchCriteriaBase? searchCriteria = null)
        {
            try
            {
                return _inner.Map(parsedAlbumInfo, searchCriteria);
            }
            catch (MultipleArtistsFoundException e)
            {
                LogCollision(parsedAlbumInfo.ArtistName, e);
                return new RemoteAlbum { ParsedAlbumInfo = parsedAlbumInfo };
            }
        }

        // Keyed on artist id, so a shared CleanName cannot reach it.
        public RemoteAlbum Map(ParsedAlbumInfo parsedAlbumInfo, int artistId, IEnumerable<int> albumIds) =>
            _inner.Map(parsedAlbumInfo, artistId, albumIds);

        public List<Album> GetAlbums(ParsedAlbumInfo parsedAlbumInfo, Artist artist, SearchCriteriaBase? searchCriteria = null) =>
            _inner.GetAlbums(parsedAlbumInfo, artist, searchCriteria);

        public Album GetLocalAlbum(string filename, Artist artist) => _inner.GetLocalAlbum(filename, artist);

        // Info, not Debug: with the queue warning suppressed this is the only trace of the collision.
        // The exception already names the matching artists.
        private void LogCollision(string title, MultipleArtistsFoundException e) =>
            _logger.Info(e, "Multiple artists share a clean name for '{0}'; falling back to download history", title);
    }
}
