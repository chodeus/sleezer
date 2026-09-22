using NLog;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Plugin.Sleezer.Core.Replacements
{
    // ArtistRepository throws when two artists share a CleanName, and the throw jumps over the
    // history fallbacks in TrackedDownloadService.TrackDownload and CompletedDownloadService.Check.
    //
    // Composed, not inherited: Lidarr's develop branch added a ParsingService constructor parameter
    // the pinned submodule lacks, and a subclass binds the ctor it compiled against at runtime.
    public class SleezerParsingService : IParsingService
    {
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

        // Info, not Debug: this replaces a queue warning the user could previously see without
        // turning Debug logging on. The exception already names the matching artists.
        private void LogCollision(string title, MultipleArtistsFoundException e) =>
            _logger.Info(e, "Multiple artists share a clean name for '{0}'; falling back to download history", title);
    }
}
