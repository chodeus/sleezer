using System.Text.Json;
using NzbDrone.Core.Download;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;
using Xunit;

namespace Sleezer.Tests;

// Lidarr's health check reads the items on its own thread: a pass claimed by one refresh must read as running to the other.
public class SlskdPostProcessClaimTests
{
    private const string Dir = @"@@u\Artist\Album";

    private static SlskdDownloadItem Finished()
    {
        string source = $"[{{\"Filename\":{JsonSerializer.Serialize(Dir + "\\01.flac")},\"Size\":1000}}]";
        SlskdDownloadItem item = new(new ReleaseInfo { Source = source, Title = "t", DownloadUrl = "u" });
        SlskdDownloadFile file = new(
            Id: "01", Username: "peer", Direction: "Download", Filename: Dir + "\\01.flac",
            Size: 1000, StartOffset: 0, State: "Completed, Succeeded",
            RequestedAt: DateTime.UtcNow, EnqueuedAt: DateTime.UtcNow, StartedAt: DateTime.UtcNow,
            BytesTransferred: 1000, AverageSpeed: 0, BytesRemaining: 0,
            ElapsedTime: TimeSpan.Zero, PercentComplete: 100, RemainingTime: TimeSpan.Zero, EndedAt: null);
        item.SlskdDownloadDirectory = new SlskdDownloadDirectory(Dir, 1, [file]);
        return item;
    }

    private static DownloadItemStatus Status(SlskdDownloadItem item) =>
        SlskdStatusResolver.Resolve(item, TimeSpan.FromMinutes(30), DateTime.UtcNow).Status;

    [Fact]
    public async Task A_pass_reads_as_running_from_its_claim_before_its_task_exists()
    {
        SlskdDownloadItem item = Finished();
        using ManualResetEventSlim starting = new(), mayReturn = new();
        Task<bool> claim = Task.Run(() => item.TryRunPostProcess(() =>
        {
            starting.Set();
            mayReturn.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            return null;
        }));
        Assert.True(starting.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.Equal(DownloadItemStatus.Downloading, Status(item));

        mayReturn.Set();
        Assert.True(await claim.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(DownloadItemStatus.Completed, Status(item));
    }

    [Fact]
    public async Task A_pass_reads_as_running_until_its_task_ends()
    {
        SlskdDownloadItem item = Finished();
        TaskCompletionSource work = new();

        Assert.True(item.TryRunPostProcess(() => work.Task));
        Assert.Equal(DownloadItemStatus.Downloading, Status(item));

        work.SetResult();
        await item.PostProcess!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(DownloadItemStatus.Completed, Status(item));
    }

    [Fact]
    public void A_second_claim_starts_nothing_and_leaves_the_first_running()
    {
        SlskdDownloadItem item = Finished();
        TaskCompletionSource work = new();
        Assert.True(item.TryRunPostProcess(() => work.Task));

        int started = 0;
        Assert.False(item.TryRunPostProcess(() =>
        {
            started++;
            return null;
        }));

        Assert.Equal(0, started);
        Assert.Equal(DownloadItemStatus.Downloading, Status(item));
        work.SetResult();
    }

    [Fact]
    public void A_pass_with_nothing_to_run_completes_at_once()
    {
        SlskdDownloadItem item = Finished();

        Assert.True(item.TryRunPostProcess(() => null));

        Assert.Equal(DownloadItemStatus.Completed, Status(item));
    }

    // GetItems reports PostProcessFailure as a failure; the pass must end, not read as running forever.
    [Fact]
    public void A_pass_that_cannot_start_ends_with_a_failure()
    {
        SlskdDownloadItem item = Finished();

        Assert.Throws<InvalidOperationException>(() => item.TryRunPostProcess(() => throw new InvalidOperationException("history unreadable")));

        Assert.True(item.PostProcess!.IsCompleted);
        Assert.Equal("Post-processing could not start", item.PostProcessFailure);
    }
}
