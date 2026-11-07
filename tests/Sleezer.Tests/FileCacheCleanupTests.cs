using NzbDrone.Plugin.Sleezer.Core.Model;
using Xunit;

namespace Sleezer.Tests;

// Opening a cache deleted every top-level .json in the chosen folder, not only the old-scheme entries.
public class FileCacheCleanupTests
{
    [Fact]
    public void Opening_a_cache_removes_old_entries_and_leaves_other_files()
    {
        string folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"cache-{Guid.NewGuid():N}")).FullName;
        try
        {
            string oldEntry = Path.Combine(folder, new string('a', 64) + ".json");
            string other = Path.Combine(folder, "dynamic-threshold.json");
            File.WriteAllText(oldEntry, "{}");
            File.WriteAllText(other, "{}");

            _ = new FileCache(folder);

            Assert.False(File.Exists(oldEntry));
            Assert.True(File.Exists(other));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
