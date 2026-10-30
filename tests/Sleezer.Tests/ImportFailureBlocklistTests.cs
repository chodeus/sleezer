using NLog;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Blocklisting;
using Xunit;

namespace Sleezer.Tests;

// A failed import blocklists a Soulseek release for good, as marking a download failed yourself does; a peer's
// transfer failure still decays.
public class ImportFailureBlocklistTests
{
    private static readonly ReleaseInfo Release = new() { Guid = "36_Slskd-peer-folder", Title = "Moonward - Glass Harbour [FLAC]", Indexer = "Slskd" };

    private static AlbumImportIncompleteEvent Incomplete(string protocol) => new(new TrackedDownload
    {
        Protocol = protocol,
        RemoteAlbum = new RemoteAlbum { Release = Release, Artist = new Artist { Id = 1 }, Albums = [new Album { Id = 10 }], ParsedAlbumInfo = new ParsedAlbumInfo() },
    });

    private static void Handle(FakeBlocklistRepository repo, string protocol) =>
        new ImportFailureBlocklist(repo, LogManager.GetLogger("tests")).Handle(Incomplete(protocol));

    [Fact]
    public void a_failed_import_blocklists_the_release_for_good()
    {
        FakeBlocklistRepository repo = new();
        Handle(repo, "SoulseekDownloadProtocol");

        Blocklist row = Assert.Single(repo.All());
        Assert.Equal(Release.Guid, row.TorrentInfoHash);
        Assert.Equal([10], row.AlbumIds);

        row.Date = DateTime.UtcNow.AddDays(-90);
        Assert.True(new SoulseekBlocklist(repo).IsBlocklisted(1, Release));
    }

    [Fact]
    public void a_second_failed_import_adds_no_second_row()
    {
        FakeBlocklistRepository repo = new();
        Handle(repo, "SoulseekDownloadProtocol");
        Handle(repo, "SoulseekDownloadProtocol");

        Assert.Single(repo.All());
    }

    [Fact]
    public void a_failed_import_from_another_protocol_is_left_to_lidarr()
    {
        FakeBlocklistRepository repo = new();
        Handle(repo, "QobuzDownloadProtocol");

        Assert.Empty(repo.All());
    }

    [Theory]
    [InlineData(SoulseekBlocklist.ManualFailureMessage)]
    [InlineData("Import failed: Item removed by Queue Cleaner.")]
    public void marking_a_download_failed_yourself_or_by_queue_cleaner_never_decays(string reason)
    {
        FakeBlocklistRepository repo = new();
        repo.Add(new Blocklist { ArtistId = 1, TorrentInfoHash = Release.Guid, Date = DateTime.UtcNow.AddDays(-90), Message = reason });

        Assert.True(new SoulseekBlocklist(repo).IsBlocklisted(1, Release));
    }

    [Fact]
    public void a_transfer_failure_still_decays()
    {
        FakeBlocklistRepository repo = new();
        repo.Add(new Blocklist { ArtistId = 1, TorrentInfoHash = Release.Guid, Date = DateTime.UtcNow.AddHours(-2), Message = "Peer is offline" });

        Assert.False(new SoulseekBlocklist(repo).IsBlocklisted(1, Release));
    }
}
