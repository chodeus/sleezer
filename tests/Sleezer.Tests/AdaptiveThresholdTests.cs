using NzbDrone.Plugin.Sleezer.Metadata.Proxy.MetadataProvider.Mixed;
using Xunit;

namespace Sleezer.Tests;

// Maximal usable threshold was never read: the dynamic threshold ran up to a hard-coded 15.
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

    // Storing Path is picked as a folder, and a first run has no file yet: the weights were never saved.
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
}
