using NzbDrone.Common.Messaging;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Blocklisting;
using NzbDrone.Plugin.Sleezer.Core.Qobuz;
using Xunit;

namespace Sleezer.Tests;

// Runs Lidarr's own FailedDownloadService, so the reason a client reports is shown to reach the blocklist row.
public class AlbumWideFailureTests
{
    private const string Grabbed = "1_Qobuz-abc123xyz-FLACLossless";
    private const string OtherTier = "1_Qobuz-abc123xyz-FLACHiRes24Bit96kHz";

    private static QobuzBlocklist BlocklistAfterFailure(string? clientMessage)
    {
        EntityHistory grab = new()
        {
            ArtistId = 1,
            AlbumId = 10,
            DownloadId = "dl-1",
            SourceTitle = "Artist - Album",
            Data = new() { ["guid"] = Grabbed, ["protocol"] = "QobuzDownloadProtocol", ["indexer"] = "Qobuz" },
        };
        CapturingEvents events = new();
        TrackedDownload download = new()
        {
            State = TrackedDownloadState.DownloadFailedPending,
            DownloadItem = new DownloadClientItem { DownloadId = "dl-1", Status = DownloadItemStatus.Failed, Message = clientMessage },
        };

        new FailedDownloadService(new GrabHistory(grab), events).ProcessFailed(download);

        FakeBlocklistRepository repo = new();
        QobuzBlocklist blocklist = new(repo);
        repo.Add(blocklist.GetBlocklist(Assert.Single(events.Failed)));
        return blocklist;
    }

    [Fact]
    public void An_album_with_unstreamable_tracks_is_blocked_at_every_tier()
    {
        QobuzBlocklist blocklist = BlocklistAfterFailure(QobuzAlbumFailure.Reason(unstreamableTracks: 2, requireCompleteAlbum: true));

        Assert.True(blocklist.IsBlocklisted(1, new ReleaseInfo { Guid = OtherTier }));
    }

    [Fact]
    public void A_failure_with_no_reason_blocks_only_the_grabbed_tier()
    {
        QobuzBlocklist blocklist = BlocklistAfterFailure(null);

        Assert.True(blocklist.IsBlocklisted(1, new ReleaseInfo { Guid = Grabbed }));
        Assert.False(blocklist.IsBlocklisted(1, new ReleaseInfo { Guid = OtherTier }));
    }

    [Theory]
    [InlineData(2, true, true)]
    [InlineData(0, true, false)]
    [InlineData(2, false, false)]
    public void Unstreamable_tracks_rule_out_the_album_only_when_it_must_be_complete(int unstreamable, bool requireCompleteAlbum, bool albumWide) =>
        Assert.Equal(albumWide, AlbumWideFailure.Covers(QobuzAlbumFailure.Reason(unstreamable, requireCompleteAlbum)));

    private sealed class CapturingEvents : IEventAggregator
    {
        public List<DownloadFailedEvent> Failed { get; } = [];

        public void PublishEvent<TEvent>(TEvent @event) where TEvent : class, IEvent
        {
            if (@event is DownloadFailedEvent failed)
                Failed.Add(failed);
        }
    }

    private sealed class GrabHistory(EntityHistory grab) : IHistoryService
    {
        public List<EntityHistory> Find(string downloadId, EntityHistoryEventType eventType) =>
            downloadId == grab.DownloadId && eventType == EntityHistoryEventType.Grabbed ? [grab] : [];

        public PagingSpec<EntityHistory> Paged(PagingSpec<EntityHistory> pagingSpec, int[] qualities) => throw new NotImplementedException();
        public EntityHistory MostRecentForAlbum(int albumId) => throw new NotImplementedException();
        public EntityHistory MostRecentForDownloadId(string downloadId) => throw new NotImplementedException();
        public EntityHistory Get(int historyId) => throw new NotImplementedException();
        public List<EntityHistory> GetByArtist(int artistId, EntityHistoryEventType? eventType) => throw new NotImplementedException();
        public List<EntityHistory> GetByAlbum(int albumId, EntityHistoryEventType? eventType) => throw new NotImplementedException();
        public List<EntityHistory> FindByDownloadId(string downloadId) => throw new NotImplementedException();
        public string FindDownloadId(TrackImportedEvent trackedDownload) => throw new NotImplementedException();
        public List<EntityHistory> Since(DateTime date, EntityHistoryEventType? eventType) => throw new NotImplementedException();
        public void UpdateMany(IList<EntityHistory> items) => throw new NotImplementedException();
    }
}
