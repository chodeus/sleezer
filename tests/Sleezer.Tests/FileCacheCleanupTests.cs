using NzbDrone.Plugin.Sleezer.Core.Model;
using Xunit;

namespace Sleezer.Tests;

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

    [Fact]
    public async Task A_torn_entry_is_a_miss_and_is_removed()
    {
        string folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"cache-{Guid.NewGuid():N}")).FullName;
        try
        {
            FileCache cache = new(folder);
            await cache.SetAsync("key", "value", TimeSpan.FromHours(1));
            string entry = Assert.Single(Directory.GetFiles(folder, "*.json", SearchOption.AllDirectories));
            await File.WriteAllTextAsync(entry, "{\"Data\":\"val");

            Assert.Null(await cache.GetAsync<string>("key"));
            Assert.False(File.Exists(entry));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task An_entry_that_cannot_be_read_now_is_kept()
    {
        string folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"cache-{Guid.NewGuid():N}")).FullName;
        try
        {
            FileCache cache = new(folder);
            await cache.SetAsync("key", "value", TimeSpan.FromHours(1));
            string entry = Assert.Single(Directory.GetFiles(folder, "*.json", SearchOption.AllDirectories));

            using (new FileStream(entry, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.Null(await cache.GetAsync<string>("key"));

            Assert.Equal("value", await cache.GetAsync<string>("key"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_write_that_cannot_land_leaves_no_temp_file()
    {
        string folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"cache-{Guid.NewGuid():N}")).FullName;
        try
        {
            FileCache cache = new(folder);
            await cache.SetAsync("key", "value", TimeSpan.FromHours(1));
            string entry = Assert.Single(Directory.GetFiles(folder, "*.json", SearchOption.AllDirectories));
            File.Delete(entry);
            Directory.CreateDirectory(entry);

            Exception thrown = await Assert.ThrowsAnyAsync<Exception>(() => cache.SetAsync("key", "value", TimeSpan.FromHours(1)));
            Assert.True(thrown is IOException or UnauthorizedAccessException, thrown.GetType().Name);

            Assert.Empty(Directory.GetFiles(folder, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
