using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Blocklisting;
using Xunit;

namespace Sleezer.Tests;

// Native-blocklist repair: the plugin read PascalCase keys from a Data dict that
// round-trips through Lidarr's CamelCase DictionaryKeyPolicy, so every row was
// written with empty protocol/indexer/hash and never matched. Plus the escalating
// decay for Soulseek's transient failures.
public class SlskdBlocklistTests
{
    // Only BlocklistedByTorrentInfoHash + Add are exercised; the rest satisfy
    // the wide IBasicRepository surface.
    private sealed class FakeRepo : IBlocklistRepository
    {
        private readonly List<Blocklist> _rows = [];
        public void Add(Blocklist b) => _rows.Add(b);

        public List<Blocklist> BlocklistedByTorrentInfoHash(int artistId, string hash) =>
            [.. _rows.Where(b => b.ArtistId == artistId && b.TorrentInfoHash != null && b.TorrentInfoHash.Contains(hash))];
        public List<Blocklist> BlocklistedByTitle(int artistId, string sourceTitle) => [];
        public List<Blocklist> BlocklistedByArtists(List<int> artistIds) => [];
        public void DeleteForArtists(List<int> artistIds) { }

        public IEnumerable<Blocklist> All() => _rows;
        public int Count() => _rows.Count;
        public Blocklist Find(int id) => throw new NotImplementedException();
        public Blocklist Get(int id) => throw new NotImplementedException();
        public IEnumerable<Blocklist> Get(IEnumerable<int> ids) => throw new NotImplementedException();
        public Blocklist Insert(Blocklist model) { _rows.Add(model); return model; }
        public Blocklist Update(Blocklist model) => model;
        public Blocklist Upsert(Blocklist model) => model;
        public void SetFields(Blocklist model, params System.Linq.Expressions.Expression<Func<Blocklist, object>>[] properties) { }
        public void SetFields(IList<Blocklist> models, params System.Linq.Expressions.Expression<Func<Blocklist, object>>[] properties) { }
        public void Delete(Blocklist model) { }
        public void Delete(int id) { }
        public void InsertMany(IList<Blocklist> models) => _rows.AddRange(models);
        public void UpdateMany(IList<Blocklist> models) { }
        public void DeleteMany(List<Blocklist> models) { }
        public void DeleteMany(IEnumerable<int> ids) { }
        public void Purge(bool vacuum = false) { }
        public bool HasItems() => _rows.Count > 0;
        public Blocklist Single() => throw new NotImplementedException();
        public Blocklist SingleOrDefault() => throw new NotImplementedException();
        public PagingSpec<Blocklist> GetPaged(PagingSpec<Blocklist> pagingSpec) => pagingSpec;
    }

    private static DownloadFailedEvent FailedEvent(string guid, Dictionary<string, string> data) => new()
    {
        ArtistId = 1,
        AlbumIds = [10],
        SourceTitle = "Artist - Album",
        Message = "failed",
        Data = data,
    };

    [Fact]
    public void GetBlocklist_reads_camelcase_keys_from_rehydrated_data()
    {
        FakeRepo repo = new();
        DeezerBlocklist bl = new(repo);
        Blocklist row = bl.GetBlocklist(FailedEvent("g", new()
        {
            ["protocol"] = "DeezerDownloadProtocol",
            ["indexer"] = "Deezer",
            ["guid"] = "36_Slskd-abc",
            ["size"] = "123",
        }));

        Assert.Equal("DeezerDownloadProtocol", row.Protocol);
        Assert.Equal("Deezer", row.Indexer);
        Assert.Equal("36_Slskd-abc", row.TorrentInfoHash);
        Assert.Equal(123, row.Size);
    }

    [Fact]
    public void GetBlocklist_still_reads_pascalcase_keys()
    {
        FakeRepo repo = new();
        SoulseekBlocklist bl = new(repo);
        Blocklist row = bl.GetBlocklist(FailedEvent("g", new()
        {
            ["Protocol"] = "SoulseekDownloadProtocol",
            ["Guid"] = "36_Slskd-xyz",
        }));

        Assert.Equal("SoulseekDownloadProtocol", row.Protocol);
        Assert.Equal("36_Slskd-xyz", row.TorrentInfoHash);
    }

    [Fact]
    public void Soulseek_block_decays_on_the_escalating_window()
    {
        FakeRepo repo = new();
        SoulseekBlocklist bl = new(repo);
        ReleaseInfo release = new() { Guid = "36_Slskd-hash1", DownloadProtocol = new NzbDrone.Core.Indexers.SoulseekDownloadProtocol().GetType().Name };

        // One failure 2h ago: first-tier window is 1h, so it has decayed.
        repo.Add(new Blocklist { ArtistId = 1, TorrentInfoHash = "36_Slskd-hash1", Date = DateTime.UtcNow.AddHours(-2) });
        Assert.False(bl.IsBlocklisted(1, release));

        // Two failures, newest 2h ago: second-tier window is 6h, still blocked.
        repo.Add(new Blocklist { ArtistId = 1, TorrentInfoHash = "36_Slskd-hash1", Date = DateTime.UtcNow.AddHours(-2) });
        Assert.True(bl.IsBlocklisted(1, release));
    }

    [Fact]
    public void Soulseek_block_ignores_other_releases()
    {
        FakeRepo repo = new();
        SoulseekBlocklist bl = new(repo);
        repo.Add(new Blocklist { ArtistId = 1, TorrentInfoHash = "36_Slskd-other", Date = DateTime.UtcNow });
        Assert.False(bl.IsBlocklisted(1, new ReleaseInfo { Guid = "36_Slskd-hash1" }));
    }
}
