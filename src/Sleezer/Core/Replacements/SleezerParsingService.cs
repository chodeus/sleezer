using NLog;
using NzbDrone.Common.Extensions;
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
                return PreferSearchedAlbum(_inner.Map(parsedAlbumInfo, searchCriteria), searchCriteria);
            }
            catch (MultipleArtistsFoundException e)
            {
                LogCollision(parsedAlbumInfo.ArtistName, e);
                return new RemoteAlbum { ParsedAlbumInfo = parsedAlbumInfo };
            }
        }

        // FindByTitle gives up when two library albums share a clean title, and the inexact fallback can
        // then pick another; in a search, the searched album with that clean title is the one meant.
        internal static RemoteAlbum PreferSearchedAlbum(RemoteAlbum mapped, SearchCriteriaBase? searchCriteria)
        {
            string? title = mapped.ParsedAlbumInfo?.AlbumTitle;
            if (searchCriteria?.Albums == null || mapped.Artist == null || mapped.Artist.Id != searchCriteria.Artist?.Id || string.IsNullOrWhiteSpace(title))
                return mapped;

            string clean = title.CleanArtistName();
            Album? searched = searchCriteria.Albums.ExclusiveOrDefault(a => a.Title.CleanArtistName() == clean);
            if (searched == null || mapped.Albums is [{ } only] && only.Id == searched.Id)
                return mapped;

            mapped.Albums = [searched];
            return mapped;
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
            _logger.Info(e, "Multiple artists share a clean name for '{0}'; artist left unresolved, so Lidarr can use a grab-history record if one exists", title);
    }
}
