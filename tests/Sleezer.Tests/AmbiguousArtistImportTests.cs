using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser;
using NzbDrone.Plugin.Sleezer.Core.Replacements;
using Sleezer.Tests.Fakes;
using Xunit;

namespace Sleezer.Tests;

// Drives Lidarr's own TrackedDownloadService, unmodified, so the claim being tested is that the
// history fallback at TrackDownload's `RemoteAlbum?.Artist == null` branch becomes reachable.
public class AmbiguousArtistImportTests
{
    private const string DownloadId = "ABC123";
    private const string GrabTitle = "Some Artist - Some Album (2020) [FLAC]";

    private static readonly Logger Log = LogManager.GetLogger(nameof(AmbiguousArtistImportTests));

    private static readonly Artist Wanted = new() { Id = 42, Name = "Some Artist" };
    private static readonly Album Grabbed = new() { Id = 7, Title = "Some Album" };

    private sealed class CollidingArtistService : FakeArtistService
    {
        public override Artist FindByName(string title) => throw new MultipleArtistsFoundException(
            [new Artist { Name = "Some Artist" }, new Artist { Name = "Some  Artist" }],
            "Expected one artist, but found {0}. Matching artists: {1}", 2, title);

        // What the history fallback uses: a primary-key lookup, so the collision cannot reach it.
        public override Artist GetArtist(int artistId) => artistId == Wanted.Id ? Wanted : throw new NotSupportedException();
    }

    private static TrackedDownload Track(IParsingService parsing)
    {
        List<EntityHistory> history =
        [
            new()
            {
                DownloadId = DownloadId,
                SourceTitle = GrabTitle,
                ArtistId = Wanted.Id,
                AlbumId = Grabbed.Id,
                Artist = Wanted,
                Album = Grabbed,
                Date = DateTime.UtcNow,
                EventType = EntityHistoryEventType.Grabbed
            }
        ];

        TrackedDownloadService subject = new(parsing,
            new CacheManager(),
            new FakeHistoryService(history),
            new FakeFormatCalculator(),
            new FakeEventAggregator(),
            new FakeDownloadHistoryService(),
            new PassThroughAggregationService(),
            Log);

        return subject.TrackDownload(
            new DownloadClientDefinition { Id = 1 },
            new DownloadClientItem
            {
                DownloadId = DownloadId,
                Title = GrabTitle,
                TotalSize = 1024,
                DownloadClientInfo = new DownloadClientItemClientInfo { Name = "Test" }
            });
    }

    private static IParsingService Patched() =>
        new SleezerParsingService(
            new ParsingService(null!, new CollidingArtistService(), new FakeAlbumService([Grabbed]), null!, Log), Log);

    private static IParsingService Unpatched() =>
        new ParsingService(null!, new CollidingArtistService(), new FakeAlbumService([Grabbed]), null!, Log);

    [Fact]
    public void The_download_is_resolved_from_grab_history_when_the_artist_name_is_ambiguous()
    {
        TrackedDownload tracked = Track(Patched());

        Assert.NotNull(tracked.RemoteAlbum);
        Assert.Equal(Wanted.Id, tracked.RemoteAlbum.Artist.Id);
        Assert.Equal([Grabbed.Id], tracked.RemoteAlbum.Albums.Select(a => a.Id));
        Assert.Empty(tracked.StatusMessages);
    }

    // Control: the same flow on stock Lidarr. RemoteAlbum stays null, so CompletedDownloadService
    // .Import blocks on `RemoteAlbum == null` no matter which artist Check resolves.
    [Fact]
    public void Stock_Lidarr_leaves_the_download_unresolved_and_warns()
    {
        TrackedDownload tracked = Track(Unpatched());

        Assert.Null(tracked.RemoteAlbum);
        Assert.Contains(tracked.StatusMessages,
            m => m.Messages.Any(x => x.Contains("found multiple artists", StringComparison.OrdinalIgnoreCase)));
    }
}
