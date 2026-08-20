using System.Text.Json;
using NzbDrone.Core.Download;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;
using Xunit;

namespace Sleezer.Tests;

// A permanently-failed cue/log used to fail the WHOLE album: the resolver
// counted it in failedCount and completion never turned true, so a flaky peer's
// 2 KB log blocklisted a perfect rip. "Abandoned extra" is derived state — no
// flags — so it survives restart rehydration and RetryAttempts=0.
public class SlskdStatusResolverAbandonTests
{
    private const string Dir = @"@@u\Artist\Album";

    private static SlskdDownloadItem NewItem(params (string Name, long Size)[] files)
    {
        string source = "[" + string.Join(",", files.Select(f =>
            $"{{\"Filename\":{JsonSerializer.Serialize(Dir + "\\" + f.Name)},\"Size\":{f.Size}}}")) + "]";
        return new SlskdDownloadItem(new ReleaseInfo { Source = source, Title = "t", DownloadUrl = "u" });
    }

    private static SlskdDownloadFile Transfer(string name, string state, long size) => new(
        Id: name, Username: "peer", Direction: "Download", Filename: Dir + "\\" + name,
        Size: size, StartOffset: 0, State: state,
        RequestedAt: DateTime.UtcNow, EnqueuedAt: DateTime.UtcNow, StartedAt: DateTime.UtcNow,
        BytesTransferred: 0, AverageSpeed: 0, BytesRemaining: 0,
        ElapsedTime: TimeSpan.Zero, PercentComplete: 0, RemainingTime: TimeSpan.Zero, EndedAt: null);

    private static void Transfers(SlskdDownloadItem item, params SlskdDownloadFile[] files) =>
        item.SlskdDownloadDirectory = new SlskdDownloadDirectory(Dir, files.Length, files.ToList());

    private static SlskdFileState State(SlskdDownloadItem item, string name) => item.FileStates[Dir + "\\" + name];

    private static SlskdStatusResolver.DownloadStatus Resolve(SlskdDownloadItem item) =>
        SlskdStatusResolver.Resolve(item, TimeSpan.FromMinutes(30), DateTime.UtcNow);

    [Fact]
    public void A_terminally_errored_cue_still_completes_the_album()
    {
        SlskdDownloadItem item = NewItem(("01.flac", 1000), ("02.flac", 1000), ("03.flac", 1000), ("rip.cue", 2000));
        Transfers(item,
            Transfer("01.flac", "Completed, Succeeded", 1000),
            Transfer("02.flac", "Completed, Succeeded", 1000),
            Transfer("03.flac", "Completed, Succeeded", 1000),
            Transfer("rip.cue", "Completed, Errored", 2000));
        // RetryAttempts=0: the file is terminally failed with no retry ever fired.
        State(item, "rip.cue").UpdateMaxRetryCount(0);

        SlskdStatusResolver.DownloadStatus resolved = Resolve(item);

        Assert.Equal(DownloadItemStatus.Completed, resolved.Status);
        Assert.Contains("extra file", resolved.Message);
    }

    [Fact]
    public void A_cue_stuck_in_the_remote_queue_past_its_retries_still_completes_the_album()
    {
        SlskdDownloadItem item = NewItem(("01.flac", 1000), ("02.flac", 1000), ("rip.cue", 2000));
        Transfers(item,
            Transfer("01.flac", "Completed, Succeeded", 1000),
            Transfer("02.flac", "Completed, Succeeded", 1000),
            Transfer("rip.cue", "Queued, Remotely", 2000));
        State(item, "rip.cue").MarkRetriesExhausted();

        Assert.Equal(DownloadItemStatus.Completed, Resolve(item).Status);
    }

    [Fact]
    public void A_terminally_failed_audio_file_still_fails_the_album()
    {
        SlskdDownloadItem item = NewItem(("01.flac", 1000), ("02.flac", 1000), ("rip.cue", 2000));
        Transfers(item,
            Transfer("01.flac", "Completed, Succeeded", 1000),
            Transfer("02.flac", "Completed, Errored", 1000),
            Transfer("rip.cue", "Completed, Succeeded", 2000));
        State(item, "02.flac").MarkRetriesExhausted();

        SlskdStatusResolver.DownloadStatus resolved = Resolve(item);

        Assert.Equal(DownloadItemStatus.Failed, resolved.Status);
        Assert.Contains("02.flac", resolved.Message);
    }

    [Fact]
    public void An_abandoned_extra_contributes_nothing_to_the_size_totals()
    {
        SlskdDownloadItem item = NewItem(("01.flac", 1000), ("02.flac", 1000), ("rip.cue", 2000));
        Transfers(item,
            Transfer("01.flac", "Completed, Succeeded", 1000),
            Transfer("02.flac", "Completed, Succeeded", 1000),
            Transfer("rip.cue", "Completed, Errored", 2000));
        State(item, "rip.cue").MarkRetriesExhausted();

        Assert.Equal(2000, Resolve(item).TotalSize);
    }

    [Fact]
    public void AllAcceptedFilesCompleted_ignores_an_abandoned_extra()
    {
        SlskdDownloadItem item = NewItem(("01.flac", 1000), ("rip.cue", 2000));
        Transfers(item,
            Transfer("01.flac", "Completed, Succeeded", 1000),
            Transfer("rip.cue", "Completed, Errored", 2000));
        State(item, "rip.cue").MarkRetriesExhausted();

        Assert.True(item.AllAcceptedFilesCompleted());
    }

    [Fact]
    public void AllAcceptedFilesCompleted_still_waits_on_a_queued_audio_file()
    {
        SlskdDownloadItem item = NewItem(("01.flac", 1000), ("02.flac", 1000), ("rip.cue", 2000));
        Transfers(item,
            Transfer("01.flac", "Completed, Succeeded", 1000),
            Transfer("02.flac", "Queued, Remotely", 1000),
            Transfer("rip.cue", "Completed, Errored", 2000));
        State(item, "rip.cue").MarkRetriesExhausted();

        Assert.False(item.AllAcceptedFilesCompleted());
    }
}

public class SlskdNonAudioBasenamesTests
{
    private static SlskdDownloadItem NewItem(params string[] filenames)
    {
        string source = "[" + string.Join(",", filenames.Select(f =>
            $"{{\"Filename\":{JsonSerializer.Serialize(f)},\"Size\":1000}}")) + "]";
        return new SlskdDownloadItem(new ReleaseInfo { Source = source, Title = "t", DownloadUrl = "u" });
    }

    [Fact]
    public void Only_the_non_audio_basenames_are_returned()
    {
        SlskdDownloadItem item = NewItem(
            @"@@u\Artist\Album\01 - Track.flac",
            @"@@u\Artist\Album\rip.cue",
            @"@@u\Artist\Album\rip.log");

        Assert.Equal(new[] { "rip.cue", "rip.log" }, item.NonAudioBasenames());
    }

    [Fact]
    public void A_pure_audio_grab_has_no_extras()
    {
        SlskdDownloadItem item = NewItem(@"@@u\A\B\01.flac", @"@@u\A\B\02.mp3", @"@@u\A\B\03.m4a");

        Assert.Empty(item.NonAudioBasenames());
    }
}
