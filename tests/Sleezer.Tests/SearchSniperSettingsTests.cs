using NzbDrone.Plugin.Sleezer.Metadata.ScheduledTasks.SearchSniper;
using Xunit;

namespace Sleezer.Tests;

public class SearchSniperSettingsTests
{
    // PickRecentAlbums starts the window at UtcNow.AddDays(-RecentReleaseDays).
    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(30, true)]
    [InlineData(36500, true)]
    [InlineData(36501, false)]
    [InlineData(1_000_000, false)]
    public void recent_release_days_keep_the_window_start_representable(int days, bool valid)
    {
        SearchSniperTaskSettings settings = new() { RecentReleaseDays = days };

        Assert.Equal(valid, settings.Validate().Errors.All(e => e.PropertyName != nameof(SearchSniperTaskSettings.RecentReleaseDays)));
        if (valid)
            DateTime.UtcNow.AddDays(-days);
    }
}
