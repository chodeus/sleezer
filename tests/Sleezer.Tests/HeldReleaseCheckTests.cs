using NzbDrone.Core.Music;
using NzbDrone.Plugin.Sleezer.Core.Model;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using Xunit;

namespace Sleezer.Tests;

public class HeldReleaseCheckTests
{
    // Each entry is (length in seconds, has a file on disk).
    private static List<Track> Held(params (int Seconds, bool HasFile)[] tracks) =>
        [.. tracks.Select((t, i) => new Track { Duration = t.Seconds * 1000, TrackFileId = t.HasFile ? i + 1 : 0 })];

    private static StoreReleaseInfo Offer(params int[] seconds) =>
        new() { Title = "x", TrackCount = seconds.Length, TrackDurationsSeconds = seconds };

    // Make It Make Sense: a 1-track single offered for the 3-track single already on disk.
    [Fact]
    public void A_smaller_product_than_the_files_on_disk_is_rejected()
    {
        string? reason = HeldReleaseCheck.Reason(Offer(189), Held((189, true), (200, true), (210, true)));

        Assert.Equal("1 track(s) offered, but 3 of this album's tracks are already on disk", reason);
    }

    [Fact]
    public void An_album_with_no_files_is_never_judged()
    {
        Assert.Null(HeldReleaseCheck.Reason(Offer(100), Held((189, false), (200, false), (210, false))));
    }

    // Alan Walker Dust: 3 of 4 on disk, the missing original offered as a 4-track product.
    [Fact]
    public void A_product_covering_a_partly_held_release_is_accepted()
    {
        Assert.Null(HeldReleaseCheck.Reason(Offer(191, 162, 280, 203), Held((191, false), (162, true), (280, true), (203, true))));
    }

    [Fact]
    public void A_same_size_product_within_mastering_drift_is_accepted()
    {
        Assert.Null(HeldReleaseCheck.Reason(Offer(214, 228), Held((214, true), (221, true))));
    }

    // Blank Space (Piano Version): 253 s offered for the 232 s single on disk.
    [Fact]
    public void A_same_size_product_with_a_different_recording_is_rejected()
    {
        string? reason = HeldReleaseCheck.Reason(Offer(253), Held((232, true)));

        Assert.Equal("track 1 (4:13) matches no track on the release already on disk", reason);
    }

    // Afrojack All Night: the 9-track remixes offered over the 5-track release on disk.
    [Fact]
    public void A_bigger_product_is_left_to_Lidarr_even_with_unknown_tracks()
    {
        Assert.Null(HeldReleaseCheck.Reason(Offer(204, 194, 189, 188, 232, 253, 314, 248, 250), Held((204, true), (194, true), (189, true), (188, true), (232, true))));
    }

    [Fact]
    public void Missing_lengths_are_unjudgeable()
    {
        Assert.Null(HeldReleaseCheck.Reason(new StoreReleaseInfo { Title = "x", TrackCount = 1 }, Held((232, true))));
        Assert.Null(HeldReleaseCheck.Reason(Offer(253), Held((0, true))));
        Assert.Null(HeldReleaseCheck.Reason(Offer(0), Held((232, true))));
    }

    [Fact]
    public void An_unknown_track_count_is_unjudgeable()
    {
        Assert.Null(HeldReleaseCheck.Reason(new StoreReleaseInfo { Title = "x", TrackDurationsSeconds = [253] }, Held((232, true), (240, true))));
    }
}
