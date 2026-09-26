using NzbDrone.Core.Music;
using NzbDrone.Plugin.Sleezer.Core.Model;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using Xunit;

namespace Sleezer.Tests;

public class HeldReleaseCheckTests
{
    // Each entry is (length in seconds, has a file on disk); files hang off the monitored release.
    private static List<Track> Release(params (int Seconds, bool HasFile)[] tracks) =>
        [.. tracks.Select((t, i) => new Track { Duration = t.Seconds * 1000, TrackFileId = t.HasFile ? i + 1 : 0 })];

    private static StoreReleaseInfo Offer(params int[] seconds) =>
        new() { Title = "x", TrackCount = seconds.Length, TrackDurationsSeconds = seconds };

    // Make It Make Sense: a 1-track FLAC single for the 3-track MP3 single on disk, which the album also has as a 1-track release.
    [Fact]
    public void A_smaller_product_matching_another_release_is_accepted()
    {
        List<Track> album = [.. Release((189, true), (200, true), (210, true)), .. Release((189, false))];

        Assert.Null(HeldReleaseCheck.Reason(Offer(189), album));
    }

    [Fact]
    public void An_album_with_no_files_is_never_judged()
    {
        Assert.Null(HeldReleaseCheck.Reason(Offer(100), Release((189, false), (200, false))));
    }

    [Fact]
    public void A_product_within_mastering_drift_is_accepted()
    {
        Assert.Null(HeldReleaseCheck.Reason(Offer(214, 228), Release((214, true), (221, true))));
    }

    // Blank Space (Piano Version): 253 s offered, and every release of the album runs 232 s.
    [Fact]
    public void A_different_recording_is_rejected()
    {
        string? reason = HeldReleaseCheck.Reason(Offer(253), [.. Release((232, true)), .. Release((232, false))]);

        Assert.Equal("track 1 (4:13) matches no track on any release of this album", reason);
    }

    // The recording is on a release other than the one on disk, so the import can switch to it.
    [Fact]
    public void A_track_found_on_another_release_is_accepted()
    {
        Assert.Null(HeldReleaseCheck.Reason(Offer(253), [.. Release((232, true)), .. Release((253, false))]));
    }

    // Afrojack All Night: the 9-track remixes over the 5-track release on disk, all nine on the album's 9-track release.
    [Fact]
    public void A_bigger_product_matching_a_bigger_release_is_accepted()
    {
        List<Track> album = [.. Release((204, true), (194, true), (189, true), (188, true), (232, true)),
                             .. Release((204, false), (194, false), (189, false), (188, false), (232, false), (253, false), (314, false), (248, false), (250, false))];

        Assert.Null(HeldReleaseCheck.Reason(Offer(204, 194, 189, 188, 232, 253, 314, 248, 250), album));
    }

    [Fact]
    public void Missing_lengths_are_unjudgeable()
    {
        Assert.Null(HeldReleaseCheck.Reason(new StoreReleaseInfo { Title = "x", TrackCount = 1 }, Release((232, true))));
        Assert.Null(HeldReleaseCheck.Reason(Offer(0), Release((232, true))));
    }

    // A release without lengths might hold the recording, so it can't be ruled out.
    [Fact]
    public void An_untimed_release_makes_the_album_unjudgeable()
    {
        Assert.Null(HeldReleaseCheck.Reason(Offer(253), [.. Release((232, true)), .. Release((0, false))]));
    }

    private sealed class StoreSettings(bool strict) : IStoreMatchingSettings
    {
        public bool StrictMatching { get; } = strict;
    }

    [Fact]
    public void Strict_matching_off_on_the_indexer_turns_the_check_off()
    {
        Assert.False(HeldReleaseCheck.StrictMatching(new StoreSettings(false)));
        Assert.True(HeldReleaseCheck.StrictMatching(new StoreSettings(true)));
    }

    [Fact]
    public void Unknown_or_foreign_indexer_settings_count_as_strict()
    {
        Assert.True(HeldReleaseCheck.StrictMatching(null));
        Assert.True(HeldReleaseCheck.StrictMatching(new object()));
    }
}
