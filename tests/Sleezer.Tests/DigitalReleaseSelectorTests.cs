using NzbDrone.Core.Music;
using NzbDrone.Plugin.Sleezer.Core.PostProcessing;
using Xunit;

namespace Sleezer.Tests;

// Issue #102: 22 of 24 albums carrying a Qobuz SOURCE tag sat on a non-digital release
// while a Digital Media release existed. A store download cannot be a CD or vinyl pressing,
// but Lidarr ranks by track-count distance alone, so a tying CD pressing wins.
// The steer must not go the other way: a digital pressing of a different length is a
// different edition, and stamping it locks the import onto "missing tracks".
public class DigitalReleaseSelectorTests
{
    private static AlbumRelease R(string id, int trackCount, params string[] formats) => new()
    {
        ForeignReleaseId = id,
        Title = id,
        TrackCount = trackCount,
        Media = [.. formats.Select((f, i) => new Medium { Number = i + 1, Format = f })]
    };

    private static string[] Ids(IEnumerable<AlbumRelease> releases) => [.. releases.Select(r => r.ForeignReleaseId)];

    [Fact]
    public void Ranks_only_the_digital_releases()
    {
        IReadOnlyList<AlbumRelease> ranked = DigitalReleaseSelector.Rank(
            [R("cd", 12, "CD"), R("digital", 12, "Digital Media"), R("vinyl", 12, "12\" Vinyl")], 12);

        Assert.Equal(["digital"], Ids(ranked));
    }

    [Fact]
    public void Is_empty_when_musicbrainz_has_no_digital_release()
    {
        Assert.Empty(DigitalReleaseSelector.Rank([R("cd", 12, "CD"), R("vinyl", 12, "12\" Vinyl")], 12));
    }

    // 18 files against a 24-track digital scored 0.085 — under the 0.15 threshold, because
    // missing_tracks weighs only 0.6 — and the 18-track CD matched at 0.000 lost to it.
    [Fact]
    public void Drops_a_digital_pressing_of_a_different_length()
    {
        Assert.Empty(DigitalReleaseSelector.Rank([R("cd", 18, "CD"), R("deluxe", 24, "Digital Media")], 18));
    }

    [Fact]
    public void Keeps_only_the_digital_pressings_that_fit()
    {
        IReadOnlyList<AlbumRelease> ranked = DigitalReleaseSelector.Rank(
            [R("deluxe", 18, "Digital Media"), R("standard", 12, "Digital Media"), R("ep", 4, "Digital Media")], 12);

        Assert.Equal(["standard"], Ids(ranked));
    }

    [Theory]
    [InlineData(12, 12, true)]
    [InlineData(13, 12, false)]
    [InlineData(12, 13, false)]
    public void Fits_only_a_download_of_the_same_length(int releaseTrackCount, int localTrackCount, bool expected)
    {
        Assert.Equal(expected, DigitalReleaseSelector.FitsDownload(R("x", releaseTrackCount, "Digital Media"), localTrackCount));
    }

    [Fact]
    public void A_missing_release_never_fits()
    {
        Assert.False(DigitalReleaseSelector.FitsDownload(null, 12));
    }

    // Order must be the same every run, or an album flips between pressings.
    [Fact]
    public void Orders_fitting_pressings_stably_by_id()
    {
        AlbumRelease[] candidates = [R("bbb", 12, "Digital Media"), R("aaa", 12, "Digital Media")];

        Assert.Equal(["aaa", "bbb"], Ids(DigitalReleaseSelector.Rank(candidates, 12)));
        Assert.Equal(["aaa", "bbb"], Ids(DigitalReleaseSelector.Rank(candidates.AsEnumerable().Reverse(), 12)));
    }

    // A multi-disc release only counts as digital if every medium is.
    [Fact]
    public void A_hybrid_release_does_not_count_as_digital()
    {
        Assert.False(DigitalReleaseSelector.IsDigital(R("hybrid", 20, "Digital Media", "CD")));
        Assert.Empty(DigitalReleaseSelector.Rank([R("hybrid", 20, "Digital Media", "CD")], 20));
    }

    [Fact]
    public void A_multi_disc_digital_release_counts_as_digital()
    {
        Assert.True(DigitalReleaseSelector.IsDigital(R("2cd", 24, "Digital Media", "Digital Media")));
    }

    [Fact]
    public void A_release_with_no_media_is_not_digital()
    {
        Assert.False(DigitalReleaseSelector.IsDigital(R("bare", 12)));
        Assert.False(DigitalReleaseSelector.IsDigital(null));
    }

    [Theory]
    [InlineData("digital media")]
    [InlineData("DIGITAL MEDIA")]
    public void Format_matching_is_case_insensitive(string format)
    {
        Assert.True(DigitalReleaseSelector.IsDigital(R("x", 12, format)));
    }

    [Fact]
    public void Handles_a_null_release_list()
    {
        Assert.Empty(DigitalReleaseSelector.Rank(null, 12));
    }
}
