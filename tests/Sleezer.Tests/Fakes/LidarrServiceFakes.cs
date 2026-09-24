using NzbDrone.Common.Messaging;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;

namespace Sleezer.Tests.Fakes;

// Hand-rolled because the test project carries no mocking library. Each fake implements only
// the members the path under test reaches; everything else throws so a silent widening shows up.

internal abstract class FakeArtistService : IArtistService
{
    public abstract Artist FindByName(string title);

    public virtual Artist GetArtist(int artistId) => throw new NotSupportedException();

    public Artist FindByNameInexact(string title) => null!;

    public Artist GetArtistByMetadataId(int artistMetadataId) => throw new NotSupportedException();
    public List<Artist> GetArtists(IEnumerable<int> artistIds) => throw new NotSupportedException();
    public Artist AddArtist(Artist newArtist, bool doRefresh) => throw new NotSupportedException();
    public List<Artist> AddArtists(List<Artist> newArtists, bool doRefresh) => throw new NotSupportedException();
    public Artist FindById(string foreignArtistId) => throw new NotSupportedException();
    public List<Artist> GetCandidates(string title) => throw new NotSupportedException();
    public void DeleteArtist(int artistId, bool deleteFiles, bool addImportListExclusion = false) => throw new NotSupportedException();
    public void DeleteArtists(List<int> artistIds, bool deleteFiles, bool addImportListExclusion = false) => throw new NotSupportedException();
    public List<Artist> GetAllArtists() => throw new NotSupportedException();
    public Dictionary<int, List<int>> GetAllArtistsTags() => throw new NotSupportedException();
    public List<Artist> AllForTag(int tagId) => throw new NotSupportedException();
    public Artist UpdateArtist(Artist artist, bool publishUpdatedEvent = true) => throw new NotSupportedException();
    public List<Artist> UpdateArtists(List<Artist> artist, bool useExistingRelativeFolder) => throw new NotSupportedException();
    public Dictionary<int, string> AllArtistPaths() => throw new NotSupportedException();
    public bool ArtistPathExists(string folder) => throw new NotSupportedException();
    public void RemoveAddOptions(Artist artist) => throw new NotSupportedException();
}

internal sealed class FakeAlbumService(List<Album> byId, bool titleLookupsFindNothing = false) : IAlbumService
{
    public List<Album> GetAlbums(IEnumerable<int> albumIds) => byId;

    public Album GetAlbum(int albumId) => throw new NotSupportedException();
    public List<Album> GetAlbumsByArtist(int artistId) => throw new NotSupportedException();
    public List<Album> GetNextAlbumsByArtistMetadataId(IEnumerable<int> artistMetadataIds) => throw new NotSupportedException();
    public List<Album> GetLastAlbumsByArtistMetadataId(IEnumerable<int> artistMetadataIds) => throw new NotSupportedException();
    public List<Album> GetAlbumsByArtistMetadataId(int artistMetadataId) => throw new NotSupportedException();
    public List<Album> GetAlbumsForRefresh(int artistMetadataId, List<string> foreignIds) => throw new NotSupportedException();
    public Album AddAlbum(Album newAlbum, bool doRefresh) => throw new NotSupportedException();
    public Album FindById(string foreignId) => throw new NotSupportedException();
    // Null is what AlbumRepository.FindByTitle returns when two library albums share the clean title.
    public Album FindByTitle(int artistMetadataId, string title) => titleLookupsFindNothing ? null! : throw new NotSupportedException();
    public Album FindByTitleInexact(int artistMetadataId, string title) => titleLookupsFindNothing ? null! : throw new NotSupportedException();
    public List<Album> GetCandidates(int artistMetadataId, string title) => throw new NotSupportedException();
    public void DeleteAlbum(int albumId, bool deleteFiles, bool addImportListExclusion = false) => throw new NotSupportedException();
    public List<Album> GetAllAlbums() => throw new NotSupportedException();
    public Album UpdateAlbum(Album album) => throw new NotSupportedException();
    public void SetAlbumMonitored(int albumId, bool monitored) => throw new NotSupportedException();
    public void SetMonitored(IEnumerable<int> ids, bool monitored) => throw new NotSupportedException();
    public void UpdateLastSearchTime(List<Album> albums) => throw new NotSupportedException();
    public PagingSpec<Album> AlbumsWithoutFiles(PagingSpec<Album> pagingSpec) => throw new NotSupportedException();
    public List<Album> AlbumsBetweenDates(DateTime start, DateTime end, bool includeUnmonitored) => throw new NotSupportedException();
    public List<Album> ArtistAlbumsBetweenDates(Artist artist, DateTime start, DateTime end, bool includeUnmonitored) => throw new NotSupportedException();
    public void InsertMany(List<Album> albums) => throw new NotSupportedException();
    public void UpdateMany(List<Album> albums) => throw new NotSupportedException();
    public void DeleteMany(List<Album> albums) => throw new NotSupportedException();
    public void SetAddOptions(IEnumerable<Album> albums) => throw new NotSupportedException();
    public Album FindAlbumByRelease(string albumReleaseId) => throw new NotSupportedException();
    public Album FindAlbumByTrackId(int trackId) => throw new NotSupportedException();
    public List<Album> GetArtistAlbumsWithFiles(Artist artist) => throw new NotSupportedException();
}

internal sealed class FakeHistoryService(List<EntityHistory> forDownloadId) : IHistoryService
{
    public List<EntityHistory> FindByDownloadId(string downloadId) => forDownloadId;

    public PagingSpec<EntityHistory> Paged(PagingSpec<EntityHistory> pagingSpec, int[] qualities) => throw new NotSupportedException();
    public EntityHistory MostRecentForAlbum(int albumId) => throw new NotSupportedException();
    public EntityHistory MostRecentForDownloadId(string downloadId) => throw new NotSupportedException();
    public EntityHistory Get(int historyId) => throw new NotSupportedException();
    public List<EntityHistory> GetByArtist(int artistId, EntityHistoryEventType? eventType) => throw new NotSupportedException();
    public List<EntityHistory> GetByAlbum(int albumId, EntityHistoryEventType? eventType) => throw new NotSupportedException();
    public List<EntityHistory> Find(string downloadId, EntityHistoryEventType eventType) => throw new NotSupportedException();
    public string FindDownloadId(TrackImportedEvent trackedDownload) => throw new NotSupportedException();
    public List<EntityHistory> Since(DateTime date, EntityHistoryEventType? eventType) => throw new NotSupportedException();
    public void UpdateMany(IList<EntityHistory> items) => throw new NotSupportedException();
}

internal sealed class FakeDownloadHistoryService : IDownloadHistoryService
{
    public DownloadHistory GetLatestDownloadHistoryItem(string downloadId) => null!;

    public bool DownloadAlreadyImported(string downloadId) => throw new NotSupportedException();
    public DownloadHistory GetLatestGrab(string downloadId) => throw new NotSupportedException();
}

internal sealed class FakeEventAggregator : IEventAggregator
{
    public void PublishEvent<TEvent>(TEvent @event)
        where TEvent : class, IEvent
    {
    }
}

// The real one null-checks and swallows augmenter failures; identity is enough here.
internal sealed class PassThroughAggregationService : NzbDrone.Core.Download.Aggregation.IRemoteAlbumAggregationService
{
    public RemoteAlbum Augment(RemoteAlbum remoteAlbum) => remoteAlbum;
}

internal sealed class FakeFormatCalculator : ICustomFormatCalculationService
{
    public List<CustomFormat> ParseCustomFormat(RemoteAlbum remoteAlbum, long size) => [];

    public List<CustomFormat> ParseCustomFormat(TrackFile trackFile, Artist artist) => throw new NotSupportedException();
    public List<CustomFormat> ParseCustomFormat(TrackFile trackFile) => throw new NotSupportedException();
    public List<CustomFormat> ParseCustomFormat(Blocklist blocklist, Artist artist) => throw new NotSupportedException();
    public List<CustomFormat> ParseCustomFormat(EntityHistory history, Artist artist) => throw new NotSupportedException();
    public List<CustomFormat> ParseCustomFormat(LocalTrack localTrack) => throw new NotSupportedException();
}
