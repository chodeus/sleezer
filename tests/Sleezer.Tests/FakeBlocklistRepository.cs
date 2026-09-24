using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Datastore;

namespace Sleezer.Tests;

// In-memory IBlocklistRepository. The hash query mirrors BlocklistRepository's Contains match.
internal sealed class FakeBlocklistRepository : IBlocklistRepository
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
