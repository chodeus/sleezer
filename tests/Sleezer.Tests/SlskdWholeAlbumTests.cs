using System.Text.Json;
using NzbDrone.Core.Download;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek;
using Xunit;

namespace Sleezer.Tests;

// An album imports whole or not at all: a file slskd won't fetch holds it, and a refusal the grab can't take over refuses the grab.
public class SlskdWholeAlbumTests
{
    private const string Dir = @"@@peer\Artist\Album";
    private const string Folder = "Artist - Album";
    private static readonly DateTime Now = DateTime.UtcNow;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(30);

    private static SlskdDownloadItem Album(DateTime trackedSince, params string[] names) =>
        new(new ReleaseInfo
        {
            Source = "[" + string.Join(",", names.Select(n => $"{{\"Filename\":{JsonSerializer.Serialize(Dir + "\\" + n)},\"Size\":1000}}")) + "]",
            Title = "t",
            DownloadUrl = "u"
        })
        {
            TrackedSinceUtc = trackedSince,
            Username = "peer",
            EnqueueDestination = Folder
        };

    private static SlskdDownloadFile Transfer(string name, string state) => new(
        Id: name, Username: "peer", Direction: "Download", Filename: Dir + "\\" + name,
        Size: 1000, StartOffset: 0, State: state,
        RequestedAt: Now, EnqueuedAt: Now, StartedAt: Now,
        BytesTransferred: 0, AverageSpeed: 0, BytesRemaining: state.StartsWith("Completed") ? 0 : 1000,
        ElapsedTime: TimeSpan.Zero, PercentComplete: 0, RemainingTime: TimeSpan.Zero, EndedAt: null);

    private static SlskdDownloadItem Polled(SlskdDownloadItem item, params SlskdDownloadFile[] transfers)
    {
        item.SlskdDownloadDirectory = new SlskdDownloadDirectory(Dir, transfers.Length, [.. transfers]);
        return item;
    }

    private static SlskdStatusResolver.DownloadStatus Resolve(SlskdDownloadItem item) =>
        SlskdStatusResolver.Resolve(item, Timeout, Now);

    private static SlskdEnqueueFailure Refusal(string name, string message = "Skipped: Already in progress") => new(Dir + "\\" + name, message);

    [Fact]
    public void A_file_with_no_transfer_holds_the_album_and_is_named()
    {
        SlskdDownloadItem item = Polled(Album(Now, "01.flac", "02.flac"), Transfer("01.flac", "Completed, Succeeded"));

        SlskdStatusResolver.DownloadStatus resolved = Resolve(item);

        Assert.Equal(DownloadItemStatus.Queued, resolved.Status);
        Assert.Equal("No slskd transfer for 1 file(s): 02.flac", resolved.Message);
        Assert.Equal(2000, resolved.TotalSize);
        Assert.Equal(1000, resolved.RemainingSize);
    }

    [Theory]
    [InlineData(59, DownloadItemStatus.Queued)]
    [InlineData(61, DownloadItemStatus.Failed)]
    public void A_file_with_no_transfer_fails_the_album_once_idle_twice_the_timeout(int minutesTracked, DownloadItemStatus expected)
    {
        SlskdDownloadItem item = Polled(Album(Now.AddMinutes(-minutesTracked), "01.flac", "02.flac"), Transfer("01.flac", "Completed, Succeeded"));

        Assert.Equal(expected, Resolve(item).Status);
    }

    [Theory]
    [InlineData("02.wv")]
    [InlineData("02.dsf")]
    [InlineData("02.dff")]
    public void A_lossless_track_the_codec_map_does_not_name_still_holds_the_album(string missing)
    {
        SlskdDownloadItem item = Polled(Album(Now, "01.flac", missing), Transfer("01.flac", "Completed, Succeeded"));

        Assert.Equal(DownloadItemStatus.Queued, Resolve(item).Status);
    }

    [Fact]
    public void An_extra_with_no_transfer_never_holds_the_album()
    {
        SlskdDownloadItem item = Polled(Album(Now, "01.flac", "rip.cue"), Transfer("01.flac", "Completed, Succeeded"));

        Assert.Equal(DownloadItemStatus.Completed, Resolve(item).Status);
    }

    [Fact]
    public void A_file_handed_to_a_regrab_is_not_waited_for()
    {
        SlskdDownloadItem item = Album(Now, "01.flac", "02.flac");
        item.HandOver([Dir + @"\02.flac"]);

        Assert.Equal(DownloadItemStatus.Completed, Resolve(Polled(item, Transfer("01.flac", "Completed, Succeeded"))).Status);
    }

    [Fact]
    public void A_file_a_superseded_attempt_is_still_fetching_is_taken_over()
    {
        SlskdDownloadItem stale = Polled(Album(Now, "01.flac", "02.flac"), Transfer("01.flac", "Completed, Succeeded"), Transfer("02.flac", "Queued, Remotely"));

        (List<(SlskdDownloadItem Holder, string Filename)> takenOver, List<SlskdEnqueueFailure> refused) =
            SlskdEnqueueRefusals.Split([Refusal("02.flac")], [stale], "peer", Folder);

        Assert.Equal([(stale, Dir + @"\02.flac")], takenOver);
        Assert.Empty(refused);
    }

    [Theory]
    [InlineData("Completed, Errored", "peer", Folder, Folder)]
    [InlineData("Queued, Remotely", "another peer", Folder, Folder)]
    [InlineData("Queued, Remotely", "peer", "Artist - Other Album", Folder)]
    [InlineData("Queued, Remotely", "peer", Folder, null)]
    public void A_refusal_no_superseded_attempt_is_fetching_into_this_folder_refuses_the_grab(string staleState, string username, string destination, string? staleFolder)
    {
        SlskdDownloadItem stale = Polled(Album(Now, "01.flac", "02.flac"), Transfer("02.flac", staleState));
        stale.EnqueueDestination = staleFolder;

        (List<(SlskdDownloadItem Holder, string Filename)> takenOver, List<SlskdEnqueueFailure> refused) =
            SlskdEnqueueRefusals.Split([Refusal("02.flac")], [stale], username, destination);

        Assert.Empty(takenOver);
        Assert.Equal([Dir + @"\02.flac"], refused.Select(f => f.Filename));
    }

    [Fact]
    public void Only_the_refusals_a_superseded_attempt_holds_are_taken_over()
    {
        SlskdDownloadItem stale = Polled(Album(Now, "01.flac", "02.flac"), Transfer("01.flac", "InProgress"));

        (List<(SlskdDownloadItem Holder, string Filename)> takenOver, List<SlskdEnqueueFailure> refused) =
            SlskdEnqueueRefusals.Split([Refusal("01.flac"), Refusal("02.flac", "Error: peer offline")], [stale], "peer", Folder);

        Assert.Equal([(stale, Dir + @"\01.flac")], takenOver);
        Assert.Equal([Dir + @"\02.flac"], refused.Select(f => f.Filename));
    }

    [Fact]
    public void A_file_that_failed_to_start_is_not_taken_over()
    {
        SlskdDownloadItem stale = Polled(Album(Now, "01.flac", "02.flac"), Transfer("02.flac", "Queued, Remotely"));

        (List<(SlskdDownloadItem Holder, string Filename)> takenOver, List<SlskdEnqueueFailure> refused) =
            SlskdEnqueueRefusals.Split([Refusal("02.flac", "Error: peer offline")], [stale], "peer", Folder);

        Assert.Empty(takenOver);
        Assert.Single(refused);
    }

    // Its removal already took its snapshot of transfers and would cancel the one handed over.
    [Fact]
    public void An_attempt_already_being_removed_hands_nothing_over()
    {
        SlskdDownloadItem stale = Polled(Album(Now, "01.flac", "02.flac"), Transfer("02.flac", "Queued, Remotely"));
        stale.MarkRemoved();

        (List<(SlskdDownloadItem Holder, string Filename)> takenOver, List<SlskdEnqueueFailure> refused) =
            SlskdEnqueueRefusals.Split([Refusal("02.flac")], [stale], "peer", Folder);

        Assert.Empty(takenOver);
        Assert.Single(refused);
    }

    [Fact]
    public void Every_attempt_holding_the_transfer_hands_it_over()
    {
        SlskdDownloadItem first = Polled(Album(Now, "01.flac", "02.flac"), Transfer("02.flac", "Queued, Remotely"));
        SlskdDownloadItem second = Polled(Album(Now, "01.flac", "02.flac"), Transfer("02.flac", "Queued, Remotely"));

        (List<(SlskdDownloadItem Holder, string Filename)> takenOver, List<SlskdEnqueueFailure> refused) =
            SlskdEnqueueRefusals.Split([Refusal("02.flac")], [first, second], "peer", Folder);

        Assert.Equal([(first, Dir + @"\02.flac"), (second, Dir + @"\02.flac")], takenOver);
        Assert.Empty(refused);
    }

    [Fact]
    public void A_refusal_with_no_superseded_attempt_refuses_the_grab()
    {
        (List<(SlskdDownloadItem Holder, string Filename)> takenOver, List<SlskdEnqueueFailure> refused) =
            SlskdEnqueueRefusals.Split([Refusal("02.flac")], [], "peer", Folder);

        Assert.Empty(takenOver);
        Assert.Single(refused);
    }
}
