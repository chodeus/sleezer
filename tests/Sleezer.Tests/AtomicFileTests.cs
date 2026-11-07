using NzbDrone.Plugin.Sleezer.Core.Utilities;
using Xunit;

namespace Sleezer.Tests;

public sealed class AtomicFileTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"atomic-{Guid.NewGuid():N}")).FullName;

    [Fact]
    public async Task A_write_through_a_symlink_keeps_the_link_and_updates_its_target()
    {
        string target = Path.Combine(_root, "real.json");
        string link = Path.Combine(_root, "settings.json");
        File.WriteAllText(target, "old");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (UnauthorizedAccessException)
        {
            Assert.Skip("This machine can't make symbolic links (Windows without developer mode).");
        }

        AtomicFile.Write(link, "new");
        Assert.Equal("new", File.ReadAllText(target));
        Assert.NotNull(new FileInfo(link).LinkTarget);

        await AtomicFile.WriteAsync(link, "newer");
        Assert.Equal("newer", File.ReadAllText(target));
        Assert.NotNull(new FileInfo(link).LinkTarget);
    }

    [Fact]
    public void A_new_file_is_written_and_leaves_no_temp_file()
    {
        string path = Path.Combine(_root, "new.json");

        AtomicFile.Write(path, "text");

        Assert.Equal("text", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public void A_replaced_file_keeps_its_mode()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Unix file modes only.");

        string path = Path.Combine(_root, "settings.resx");
        File.WriteAllText(path, "old");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        AtomicFile.Write(path, "new");

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
