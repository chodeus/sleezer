using NLog;
using NzbDrone.Core.Download;
using NzbDrone.Core.ThingiProvider.Events;
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
        tracker.UseThreshold(1, 2);
        tracker.RecordStrike("peer", 2, 2);
        Assert.Contains("peer", tracker.GetBannedUsers());

        tracker.UseThreshold(1, 0);
        Assert.Empty(tracker.GetBannedUsers());

        tracker.UseThreshold(1, 5);
        Assert.Empty(tracker.GetBannedUsers());

        tracker.UseThreshold(1, 2);
        Assert.Contains("peer", tracker.GetBannedUsers());
    }

    [Fact]
    public void A_disabled_or_deleted_clients_count_stops_banning()
    {
        SlskdCorruptUserTracker tracker = new(LogManager.GetCurrentClassLogger());
        tracker.UseThreshold(1, 2);
        tracker.UseThreshold(2, 2);
        tracker.UseThreshold(3, 0);
        tracker.RecordStrike("peer", 2, 2);

        tracker.Handle(new ProviderUpdatedEvent<IDownloadClient>(new DownloadClientDefinition { Id = 1, Enable = false }));
        Assert.Contains("peer", tracker.GetBannedUsers());

        tracker.Handle(new ProviderDeletedEvent<IDownloadClient>(2));
        Assert.Empty(tracker.GetBannedUsers());
    }
}
