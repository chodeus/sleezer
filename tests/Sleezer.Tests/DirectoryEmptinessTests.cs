using NzbDrone.Plugin.Sleezer.Core.Utilities;
using Xunit;

namespace Sleezer.Tests;

// The empty-directory sweep authorizes a recursive delete off this check, so a
// dotfile-only or unreadable folder MUST read as non-empty (the adversarial
// safety review's confirmed-major finding).
public class DirectoryEmptinessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sleezer-empty-" + Guid.NewGuid().ToString("N"));

    public DirectoryEmptinessTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void Truly_empty_directory_is_file_free()
    {
        Assert.True(DirectoryEmptiness.IsTreeFileFree(_root));
    }

    [Fact]
    public void Empty_nested_subdirectories_are_still_file_free()
    {
        Directory.CreateDirectory(Path.Combine(_root, "CD1"));
        Directory.CreateDirectory(Path.Combine(_root, "CD2", "inner"));
        Assert.True(DirectoryEmptiness.IsTreeFileFree(_root));
    }

    [Fact]
    public void A_hidden_dotfile_keeps_the_directory_non_empty()
    {
        File.WriteAllText(Path.Combine(_root, ".DS_Store"), "x");
        Assert.False(DirectoryEmptiness.IsTreeFileFree(_root));
    }

    [Fact]
    public void A_file_in_a_nested_subdir_keeps_the_directory_non_empty()
    {
        Directory.CreateDirectory(Path.Combine(_root, "CD1"));
        File.WriteAllText(Path.Combine(_root, "CD1", "01.flac"), "x");
        Assert.False(DirectoryEmptiness.IsTreeFileFree(_root));
    }

    [Fact]
    public void A_missing_directory_fails_closed_as_non_empty()
    {
        Assert.False(DirectoryEmptiness.IsTreeFileFree(Path.Combine(_root, "does-not-exist")));
    }
}
