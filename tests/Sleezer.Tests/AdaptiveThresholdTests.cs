using NzbDrone.Plugin.Sleezer.Metadata.Proxy.MetadataProvider.Mixed;
using Xunit;

namespace Sleezer.Tests;

public class AdaptiveThresholdTests
{
    private static AdaptiveThresholdManager Poorly(string proxy)
    {
        AdaptiveThresholdManager manager = new();
        manager.Config.ProxyMetrics[proxy] = new ProxyMetrics { Calls = 10, Failures = 10, QualityScore = 0, PerformanceScore = 0, LastUpdate = DateTime.UtcNow };
        return manager;
    }

    [Theory]
    [InlineData(12)]
    [InlineData(3)]
    public void A_poorly_scoring_proxy_reaches_the_setting_and_never_passes_it(int maxThreshold)
    {
        Assert.Equal(maxThreshold, Poorly("p").GetDynamicThreshold("p", 5, maxThreshold));
    }

    [Fact]
    public void A_proxy_with_no_history_is_capped_too()
    {
        Assert.Equal(2, new AdaptiveThresholdManager().GetDynamicThreshold("p", 5, 2));
    }

    [Fact]
    public void A_first_run_saves_the_weights_into_the_picked_folder()
    {
        string folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"threshold-{Guid.NewGuid():N}")).FullName;
        try
        {
            AdaptiveThresholdManager manager = new();
            manager.LoadConfig(folder);
            manager.UpdateMetrics("p", 120, 2, success: true);
            manager.Dispose();

            Assert.True(File.Exists(Path.Combine(folder, "dynamic-threshold.json")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static void SaveOnce(string storingPath)
    {
        AdaptiveThresholdManager manager = new();
        manager.LoadConfig(storingPath);
        manager.UpdateMetrics("p", 120, 2, success: true);
        manager.Dispose();
    }

    [Fact]
    public void A_folder_not_made_yet_is_made_and_gets_the_weights()
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"threshold-{Guid.NewGuid():N}")).FullName;
        try
        {
            string folder = Path.Combine(root, "weights");
            SaveOnce(folder);

            Assert.True(File.Exists(Path.Combine(folder, "dynamic-threshold.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_file_that_cannot_be_read_is_never_saved_over()
    {
        string folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"threshold-{Guid.NewGuid():N}")).FullName;
        try
        {
            string file = Path.Combine(folder, "dynamic-threshold.json");
            File.WriteAllText(file, "{ not json");
            SaveOnce(folder);

            Assert.Equal("{ not json", File.ReadAllText(file));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void An_existing_file_set_as_the_path_is_still_used()
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"threshold-{Guid.NewGuid():N}")).FullName;
        try
        {
            string file = Path.Combine(root, "weights");
            File.WriteAllText(file, "{}");
            SaveOnce(file);

            Assert.Contains("ProxyMetrics", File.ReadAllText(file), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
