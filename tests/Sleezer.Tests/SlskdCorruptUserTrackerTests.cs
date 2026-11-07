using NLog;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;
using Xunit;

namespace Sleezer.Tests;

// The ban threshold was updated only by a new strike: turning bans off, or raising the count, changed nothing until restart.
public class SlskdCorruptUserTrackerTests
{
    [Fact]
    public void The_current_threshold_decides_who_is_banned()
    {
        SlskdCorruptUserTracker tracker = new(LogManager.GetCurrentClassLogger());
        tracker.RecordStrike("peer", 2, 2);
        Assert.Contains("peer", tracker.GetBannedUsers());

        tracker.UseThreshold(0);
        Assert.Empty(tracker.GetBannedUsers());

        tracker.UseThreshold(5);
        Assert.Empty(tracker.GetBannedUsers());

        tracker.UseThreshold(2);
        Assert.Contains("peer", tracker.GetBannedUsers());
    }
}
