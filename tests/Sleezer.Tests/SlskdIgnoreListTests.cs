using NLog;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek;
using Xunit;

namespace Sleezer.Tests;

// The Ignore List keeps blocked peers out of results: an edit must land, and a failed read must not let them back in.
public sealed class SlskdIgnoreListTests : IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ignore-{Guid.NewGuid():N}.txt");

    private void Write(string users, DateTime writtenUtc)
    {
        File.WriteAllText(_path, users);
        File.SetLastWriteTimeUtc(_path, writtenUtc);
    }

    [Fact]
    public void An_edit_that_keeps_the_file_size_is_read()
    {
        Write("alice\n", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(["alice"], SlskdIgnoreList.Users(_path, Logger)!);

        Write("bobby\n", new DateTime(2026, 1, 1, 0, 5, 0, DateTimeKind.Utc));
        Assert.Equal(["bobby"], SlskdIgnoreList.Users(_path, Logger)!);
    }

    // An exclusive lock fails the read for any user, root included; a mode change does not stop root.
    private FileStream Lock() => new(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

    [Fact]
    public void A_list_that_cannot_be_read_keeps_the_last_one_read()
    {
        Write("alice\n", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(["alice"], SlskdIgnoreList.Users(_path, Logger)!);

        Write("carol\n", new DateTime(2026, 1, 1, 0, 5, 0, DateTimeKind.Utc));
        using FileStream held = Lock();

        Assert.Equal(["alice"], SlskdIgnoreList.Users(_path, Logger)!);
    }

    [Fact]
    public void A_first_read_that_fails_throws_rather_than_ignoring_nobody()
    {
        Write("alice\n", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        using FileStream held = Lock();

        Assert.ThrowsAny<IOException>(() => SlskdIgnoreList.Users(_path, Logger));
    }

    [Fact]
    public void A_list_that_vanishes_keeps_the_last_one_read()
    {
        Write("alice\n", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(["alice"], SlskdIgnoreList.Users(_path, Logger)!);

        File.Delete(_path);

        Assert.Equal(["alice"], SlskdIgnoreList.Users(_path, Logger)!);
    }

    [Fact]
    public void No_file_ignores_nobody()
    {
        Assert.Null(SlskdIgnoreList.Users(_path, Logger));
    }

    public void Dispose() => File.Delete(_path);
}
