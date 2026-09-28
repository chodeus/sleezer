using NzbDrone.Plugin.Sleezer.Core.Utilities;
using Xunit;

namespace Sleezer.Tests;

// Lidarr installs into plugins/chodeus/sleezer; a settings path built from the project name
// (Sleezer) made a second folder on case-sensitive filesystems.
public class PluginFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sleezer-plugins-" + Guid.NewGuid().ToString("N"));

    public PluginFolderTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Theory]
    [InlineData("https://github.com/chodeus/sleezer")]
    [InlineData("https://github.com/chodeus/sleezer/")]
    public void Resolve_uses_the_owner_and_repo_casing_from_the_url(string repoUrl)
    {
        Assert.Equal(Path.Combine(_root, "chodeus", "sleezer"), PluginFolder.Resolve(_root, repoUrl));
    }

    [Fact]
    public void A_legacy_file_moves_to_the_install_folder_and_its_folder_is_removed()
    {
        // Distinct names stand in for Sleezer/sleezer so the test also holds on case-insensitive disks.
        string legacy = Write(Path.Combine(_root, "legacy", "settings.resx"), "old");
        string target = Path.Combine(_root, "install", "settings.resx");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        Assert.True(PluginFolder.AdoptLegacyFile(legacy, target));
        Assert.Equal("old", File.ReadAllText(target));
        Assert.False(Directory.Exists(Path.Combine(_root, "legacy")));
    }

    [Fact]
    public void An_existing_install_file_is_never_overwritten()
    {
        string legacy = Write(Path.Combine(_root, "chodeus", "Sleezer", "settings.resx"), "old");
        string target = Write(Path.Combine(_root, "chodeus", "sleezer", "settings.resx"), "current");

        Assert.False(PluginFolder.AdoptLegacyFile(legacy, target));
        Assert.Equal("current", File.ReadAllText(target));
    }

    [Fact]
    public void A_legacy_folder_with_other_files_is_kept()
    {
        string legacy = Write(Path.Combine(_root, "legacy", "settings.resx"), "old");
        Write(Path.Combine(_root, "legacy", "other.txt"), "x");
        string target = Path.Combine(_root, "install", "settings.resx");

        Assert.True(PluginFolder.AdoptLegacyFile(legacy, target));
        Assert.True(File.Exists(Path.Combine(_root, "legacy", "other.txt")));
    }

    [Fact]
    public void No_legacy_file_is_a_no_op()
    {
        Assert.False(PluginFolder.AdoptLegacyFile(Path.Combine(_root, "legacy", "settings.resx"), Path.Combine(_root, "install", "settings.resx")));
        Assert.False(Directory.Exists(Path.Combine(_root, "install")));
    }

    private static string Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
}
