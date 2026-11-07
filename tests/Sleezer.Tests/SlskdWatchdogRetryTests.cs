using System.Text.Json;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;
using Xunit;

namespace Sleezer.Tests;

// A file the watchdog cancelled was retried in the same peer's queue and never judged again.
public class SlskdWatchdogRetryTests
{
    private const string Dir = @"@@u\Artist\Album";
    private const string Name = Dir + @"\01.flac";

    private static SlskdDownloadFile Transfer(string id, string state) => new(
        Id: id, Username: "peer", Direction: "Download", Filename: Name,
        Size: 1000, StartOffset: 0, State: state,
        RequestedAt: DateTime.UtcNow, EnqueuedAt: DateTime.UtcNow, StartedAt: DateTime.MinValue,
        BytesTransferred: 0, AverageSpeed: 0, BytesRemaining: 1000,
        ElapsedTime: TimeSpan.Zero, PercentComplete: 0, RemainingTime: TimeSpan.Zero, EndedAt: null);

    private static SlskdDownloadItem Item(SlskdDownloadFile transfer)
    {
        SlskdDownloadItem item = new(new ReleaseInfo { Source = $"[{{\"Filename\":{JsonSerializer.Serialize(Name)},\"Size\":1000}}]", Title = "t", DownloadUrl = "u" });
        item.SlskdDownloadDirectory = new SlskdDownloadDirectory(Dir, 1, [transfer]);
        return item;
    }

    [Fact]
    public void The_cancelled_transfer_stays_cancelled_and_its_retry_is_watched()
    {
        SlskdDownloadItem item = Item(Transfer("first", "Queued, Remotely"));
        SlskdFileState state = item.FileStates[Name];
        state.MarkWatchdogCancelled();

        item.SlskdDownloadDirectory = new SlskdDownloadDirectory(Dir, 1, [Transfer("first", "Queued, Remotely")]);
        Assert.True(state.WatchdogCancelled);

        item.SlskdDownloadDirectory = new SlskdDownloadDirectory(Dir, 1, [Transfer("retry", "Queued, Remotely")]);
        Assert.False(state.WatchdogCancelled);
    }
}
