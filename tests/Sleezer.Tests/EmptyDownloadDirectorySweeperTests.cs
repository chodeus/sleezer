using NLog;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using Xunit;

namespace Sleezer.Tests;

// The shared sweeper does destructive file deletion for slskd, Deezer and Tidal,
// so its guards are exercised directly against a real temp tree.
public class EmptyDownloadDirectorySweeperTests : IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sleezer-sweep-" + Guid.NewGuid().ToString("N"));
    private readonly DateTime _now = DateTime.UtcNow;

    public EmptyDownloadDirectorySweeperTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string Dir(params string[] parts)
    {
        string p = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(p);
        // Age the whole created chain past the quiet period — creating a child
        // bumps every ancestor's mtime to now, and the sweep checks the
        // root-CHILD candidate, so that level must read as stale.
        string cur = p;
        while (!string.Equals(cur.TrimEnd(Path.DirectorySeparatorChar), _root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal))
        {
            Directory.SetLastWriteTimeUtc(cur, _now.AddHours(-1));
            cur = Path.GetDirectoryName(cur)!;
        }
        return p;
    }

    private int Prune(params string[] tracked) =>
        EmptyDownloadDirectorySweeper.Prune(_root, new HashSet<string>(tracked, StringComparer.OrdinalIgnoreCase), TimeSpan.FromMinutes(15), _now, Log);

    [Fact]
    public void Prunes_an_empty_shell()
    {
        Dir("Empty Album");
        Assert.Equal(1, Prune());
        Assert.False(Directory.Exists(Path.Combine(_root, "Empty Album")));
    }

    [Fact]
    public void Prunes_a_nested_empty_tree_bottom_up()
    {
        Dir("Artist", "Album", "CD1");
        Assert.Equal(1, Prune());
        Assert.False(Directory.Exists(Path.Combine(_root, "Artist")));
    }

    [Fact]
    public void Keeps_a_folder_with_any_file_including_dotfiles()
    {
        string d = Dir("Has Data");
        File.WriteAllText(Path.Combine(d, ".DS_Store"), "x");
        Assert.Equal(0, Prune());
        Assert.True(Directory.Exists(d));
    }

    [Fact]
    public void Keeps_a_folder_with_a_nested_file()
    {
        string d = Dir("Album", "CD1");
        File.WriteAllText(Path.Combine(d, "01.flac"), "x");
        Assert.Equal(0, Prune());
        Assert.True(Directory.Exists(Path.Combine(_root, "Album")));
    }

    [Fact]
    public void Skips_a_recently_written_folder()
    {
        string d = Dir("Fresh");
        Directory.SetLastWriteTimeUtc(d, _now.AddMinutes(-1));
        Assert.Equal(0, Prune());
        Assert.True(Directory.Exists(d));
    }

    [Fact]
    public void Skips_a_tracked_folder()
    {
        Dir("Active Artist");
        Assert.Equal(0, Prune("Active Artist"));
        Assert.True(Directory.Exists(Path.Combine(_root, "Active Artist")));
    }

    [Fact]
    public void Prunes_untracked_alongside_a_tracked_sibling()
    {
        Dir("Keep Me");
        Dir("Orphan");
        Assert.Equal(1, Prune("Keep Me"));
        Assert.True(Directory.Exists(Path.Combine(_root, "Keep Me")));
        Assert.False(Directory.Exists(Path.Combine(_root, "Orphan")));
    }

    [Fact]
    public void MaybePruneThrottled_schedules_once_then_throttles_within_the_window()
    {
        // Simulates Lidarr resolving a fresh client each poll: the static state
        // in the sweeper must throttle regardless of instance churn.
        string key = "throttle-key-" + Guid.NewGuid().ToString("N");
        DateTime now = DateTime.UtcNow;
        var empty = new HashSet<string>();

        // First poll schedules; a second poll 5 min later (within the 30-min
        // throttle) must NOT schedule again — the guarantee that broke when the
        // state lived on the transient client instance.
        Assert.True(EmptyDownloadDirectorySweeper.MaybePruneThrottled(key, _root, empty, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), now, Log));
        Assert.False(EmptyDownloadDirectorySweeper.MaybePruneThrottled(key, _root, empty, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), now.AddMinutes(5), Log));
    }

    [Fact]
    public void Missing_root_is_a_noop()
    {
        Assert.Equal(0, EmptyDownloadDirectorySweeper.Prune(Path.Combine(_root, "nope"), new HashSet<string>(), TimeSpan.FromMinutes(15), _now, Log));
    }

    [Theory]
    [InlineData("/data/downloads/deezer", "/data/downloads/deezer/Artist/Album", "Artist")]
    [InlineData("/data/downloads/slskd/downloads", "/data/downloads/slskd/downloads/Album", "Album")]
    [InlineData("/data/downloads/deezer", "/elsewhere/Artist/Album", null)]
    public void RootChildLeaf_returns_the_top_level_component(string root, string path, string? expected)
    {
        Assert.Equal(expected, EmptyDownloadDirectorySweeper.RootChildLeaf(root, path));
    }
}
