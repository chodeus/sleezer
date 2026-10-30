using FluentValidation.Results;
using NLog;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Plugin.Sleezer.Blocklisting;
using NzbDrone.Plugin.Sleezer.Notifications.QueueCleaner;
using Xunit;

namespace Sleezer.Tests;

// Queue Cleaner removes through the download client, and "Blocklist Only" writes Lidarr's row without failing the download.
public class QueueCleanerTests
{
    private static readonly Logger Log = LogManager.GetLogger("tests");

    private static EntityHistory Grab(int albumId) => new()
    {
        DownloadId = "dl-1",
        ArtistId = 1,
        AlbumId = albumId,
        SourceTitle = "Moonward - Glass Harbour [FLAC]",
        EventType = EntityHistoryEventType.Grabbed,
        Data = new() { ["guid"] = "36_Slskd-peer-folder", ["protocol"] = "SoulseekDownloadProtocol", ["indexer"] = "Slskd", ["indexerFlags"] = "Freeleech" },
    };

    [Fact]
    public void blocklist_only_writes_the_row_lidarr_would_write_for_the_grab()
    {
        DownloadFailedEvent failed = QueueCleaner.FailedEvent([Grab(10), Grab(11), Grab(10)], "Import failed: blocklisted by Queue Cleaner.")!;
        Blocklist? row = QueueCleaner.BlocklistRow(failed, [new SoulseekBlocklist(new FakeBlocklistRepository())]);

        Assert.NotNull(row);
        Assert.Equal("36_Slskd-peer-folder", row!.TorrentInfoHash);
        Assert.Equal([10, 11], row.AlbumIds);
        Assert.Equal(IndexerFlags.Freeleech, row.IndexerFlags);
        Assert.Equal("Import failed: blocklisted by Queue Cleaner.", row.Message);
    }

    [Fact]
    public void a_download_with_no_grab_in_history_has_nothing_to_blocklist() =>
        Assert.Null(QueueCleaner.FailedEvent([], "Import failed: blocklisted by Queue Cleaner."));

    [Fact]
    public void a_protocol_without_a_blocklist_gets_no_row() =>
        Assert.Null(QueueCleaner.BlocklistRow(QueueCleaner.FailedEvent([Grab(10)], "x")!, []));

    [Fact]
    public void remove_goes_through_the_download_client_and_deletes_its_data()
    {
        FakeClient client = new();
        TrackedDownload download = Download();

        QueueCleaner.RemoveThroughClient(download, new Clients(client), Log);

        (DownloadClientItem item, bool deleteData) = Assert.Single(client.Removed);
        Assert.Same(download.DownloadItem, item);
        Assert.True(deleteData);
        Assert.True(download.DownloadItem.Removed);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void a_download_that_cannot_be_removed_or_is_already_gone_is_left_alone(bool canBeRemoved, bool removed)
    {
        FakeClient client = new();

        QueueCleaner.RemoveThroughClient(Download(canBeRemoved, removed), new Clients(client), Log);

        Assert.Empty(client.Removed);
    }

    [Fact]
    public void a_client_that_fails_to_remove_leaves_the_download_unremoved()
    {
        TrackedDownload download = Download();

        QueueCleaner.RemoveThroughClient(download, new Clients(new FakeClient { Throws = new InvalidOperationException("slskd is down") }), Log);

        Assert.False(download.DownloadItem.Removed);
    }

    private static TrackedDownload Download(bool canBeRemoved = true, bool removed = false) => new()
    {
        DownloadClient = 7,
        DownloadItem = new DownloadClientItem { DownloadId = "dl-1", Title = "Moonward - Glass Harbour", CanBeRemoved = canBeRemoved, Removed = removed },
    };

    private sealed class Clients(IDownloadClient client) : IProvideDownloadClient
    {
        public IDownloadClient GetDownloadClient(string downloadProtocol, int indexerId = 0, bool filterBlockedClients = false, HashSet<int>? tags = null) => client;
        public IEnumerable<IDownloadClient> GetDownloadClients(bool filterBlockedClients = false) => [client];
        public IDownloadClient Get(int id) => id == 7 ? client : throw new InvalidOperationException($"no client {id}");
    }

    private sealed class FakeClient : IDownloadClient
    {
        public List<(DownloadClientItem Item, bool DeleteData)> Removed { get; } = [];
        public Exception? Throws { get; init; }

        public void RemoveItem(DownloadClientItem item, bool deleteData)
        {
            if (Throws != null)
                throw Throws;
            Removed.Add((item, deleteData));
        }

        public string Protocol => "SoulseekDownloadProtocol";
        public string Name => "fake";
        public Type ConfigContract => typeof(object);
        public ProviderMessage Message => throw new NotImplementedException();
        public IEnumerable<ProviderDefinition> DefaultDefinitions => [];
        public ProviderDefinition Definition { get; set; } = new DownloadClientDefinition();
        public Task<string> Download(RemoteAlbum remoteAlbum, IIndexer indexer) => throw new NotImplementedException();
        public IEnumerable<DownloadClientItem> GetItems() => throw new NotImplementedException();
        public DownloadClientItem GetImportItem(DownloadClientItem item, DownloadClientItem previousImportAttempt) => throw new NotImplementedException();
        public DownloadClientInfo GetStatus() => throw new NotImplementedException();
        public void MarkItemAsImported(DownloadClientItem downloadClientItem) => throw new NotImplementedException();
        public ValidationResult Test() => throw new NotImplementedException();
        public object RequestAction(string stage, IDictionary<string, string> query) => throw new NotImplementedException();
    }
}
