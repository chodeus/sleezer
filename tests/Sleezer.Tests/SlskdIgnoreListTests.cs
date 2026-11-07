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

    [Fact]
    public void A_list_that_cannot_be_read_keeps_the_last_one_read()
    {
        if (OperatingSystem.IsWindows())
            return;

        Write("alice\n", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(["alice"], SlskdIgnoreList.Users(_path, Logger)!);

        Write("carol\n", new DateTime(2026, 1, 1, 0, 5, 0, DateTimeKind.Utc));
        File.SetUnixFileMode(_path, UnixFileMode.None);

        Assert.Equal(["alice"], SlskdIgnoreList.Users(_path, Logger)!);
    }

    [Fact]
    public void No_file_ignores_nobody()
    {
        Assert.Null(SlskdIgnoreList.Users(_path, Logger));
    }

    public void Dispose()
    {
        if (!File.Exists(_path))
            return;
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Delete(_path);
    }
}
