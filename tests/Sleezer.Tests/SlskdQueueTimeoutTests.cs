using System.Text.Json;
using NzbDrone.Core.Download;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;
using Xunit;

namespace Sleezer.Tests;

// Timeout fails a grab whose unfinished files all wait in the peer's queue past it.
public class SlskdQueueTimeoutTests
{
    private const string Dir = @"@@u\Artist\Album";
    private static readonly DateTime Now = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(60);

    private static SlskdDownloadItem NewItem(params string[] names)
    {
        string source = "[" + string.Join(",", names.Select(n =>
            $"{{\"Filename\":{JsonSerializer.Serialize(Dir + "\\" + n)},\"Size\":1000}}")) + "]";
        return new SlskdDownloadItem(new ReleaseInfo { Source = source, Title = "t", DownloadUrl = "u" });
    }

    private static SlskdDownloadFile Transfer(string name, string state, DateTime requestedAt, DateTime enqueuedAt, DateTime startedAt) => new(
        Id: name, Username: "peer", Direction: "Download", Filename: Dir + "\\" + name,
        Size: 1000, StartOffset: 0, State: state,
        RequestedAt: requestedAt, EnqueuedAt: enqueuedAt, StartedAt: startedAt,
        BytesTransferred: 0, AverageSpeed: 0, BytesRemaining: 1000,
        ElapsedTime: TimeSpan.Zero, PercentComplete: 0, RemainingTime: TimeSpan.Zero, EndedAt: null);

    private static SlskdDownloadFile Waiting(string name, TimeSpan waited) =>
        Transfer(name, "Queued, Remotely", Now - waited, Now - waited, DateTime.MinValue);

    private static SlskdStatusResolver.DownloadStatus Resolve(TimeSpan? timeout, params SlskdDownloadFile[] files)
    {
        SlskdDownloadItem item = NewItem([.. files.Select(f => f.Filename[(Dir.Length + 1)..])]);
        item.SlskdDownloadDirectory = new SlskdDownloadDirectory(Dir, files.Length, [.. files]);
        return SlskdStatusResolver.Resolve(item, timeout, Now);
    }

    [Fact]
    public void A_grab_waiting_in_the_peers_queue_past_the_timeout_fails()
    {
        SlskdStatusResolver.DownloadStatus resolved = Resolve(Timeout, Waiting("01.flac", TimeSpan.FromMinutes(61)), Waiting("02.flac", TimeSpan.FromMinutes(90)));

        Assert.Equal(DownloadItemStatus.Failed, resolved.Status);
        Assert.Contains("remote queue", resolved.Message);
    }

    [Fact]
    public void A_finished_file_does_not_hold_the_rest_of_the_album_past_the_timeout()
    {
        SlskdDownloadFile done = Transfer("01.flac", "Completed, Succeeded", Now.AddHours(-3), Now.AddHours(-3), Now.AddHours(-2));

        Assert.Equal(DownloadItemStatus.Failed, Resolve(Timeout, done, Waiting("02.flac", TimeSpan.FromHours(3))).Status);
    }

    [Fact]
    public void A_grab_waiting_less_than_the_timeout_stays_queued()
    {
        Assert.Equal(DownloadItemStatus.Queued, Resolve(Timeout, Waiting("01.flac", TimeSpan.FromMinutes(59))).Status);
    }

    [Fact]
    public void One_file_still_inside_the_timeout_keeps_the_grab_queued()
    {
        Assert.Equal(DownloadItemStatus.Queued, Resolve(Timeout, Waiting("01.flac", TimeSpan.FromHours(3)), Waiting("02.flac", TimeSpan.FromMinutes(5))).Status);
    }

    [Fact]
    public void A_downloading_file_keeps_the_grab_going()
    {
        SlskdDownloadFile downloading = Transfer("01.flac", "InProgress", Now.AddHours(-3), Now.AddHours(-3), Now.AddMinutes(-1));

        Assert.Equal(DownloadItemStatus.Downloading, Resolve(Timeout, downloading, Waiting("02.flac", TimeSpan.FromHours(3))).Status);
    }

    [Fact]
    public void A_blank_timeout_never_fails_a_waiting_grab()
    {
        Assert.Equal(DownloadItemStatus.Queued, Resolve(null, Waiting("01.flac", TimeSpan.FromDays(3))).Status);
    }

    [Fact]
    public void A_file_without_an_enqueue_time_waits_from_its_request_time()
    {
        SlskdDownloadFile recent = Transfer("01.flac", "Queued, Remotely", Now.AddMinutes(-5), DateTime.MinValue, DateTime.MinValue);
        SlskdDownloadFile old = Transfer("01.flac", "Queued, Remotely", Now.AddHours(-2), DateTime.MinValue, DateTime.MinValue);

        Assert.Equal(DownloadItemStatus.Queued, Resolve(Timeout, recent).Status);
        Assert.Equal(DownloadItemStatus.Failed, Resolve(Timeout, old).Status);
    }

    [Fact]
    public void A_file_with_no_known_queue_time_is_never_stuck()
    {
        SlskdDownloadFile unknown = Transfer("01.flac", "Queued, Remotely", DateTime.MinValue, DateTime.MinValue, DateTime.MinValue);

        Assert.Equal(DownloadItemStatus.Queued, Resolve(Timeout, unknown).Status);
    }

    [Fact]
    public void A_retrying_file_with_no_known_activity_is_not_failed_as_idle()
    {
        SlskdDownloadFile retrying = Transfer("01.flac", "Completed, TimedOut", DateTime.MinValue, DateTime.MinValue, DateTime.MinValue);

        Assert.NotEqual(DownloadItemStatus.Failed, Resolve(Timeout, retrying).Status);
    }
}
