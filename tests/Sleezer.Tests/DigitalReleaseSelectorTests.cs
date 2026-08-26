using NzbDrone.Core.Music;
using NzbDrone.Plugin.Sleezer.Core.PostProcessing;
using Xunit;

namespace Sleezer.Tests;

// Issue #102: 22 of 24 albums carrying a Qobuz SOURCE tag sat on a non-digital release
// while a Digital Media release existed. A store download cannot be a CD or vinyl pressing,
// but Lidarr ranks by track-count distance alone, so a tying CD pressing wins.
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

    // The whole point of returning a list: a near-miss on the standard edition must still
    // leave the deluxe to try.
    [Fact]
    public void Orders_every_digital_pressing_by_track_count_proximity()
    {
        IReadOnlyList<AlbumRelease> ranked = DigitalReleaseSelector.Rank(
            [R("deluxe", 18, "Digital Media"), R("standard", 12, "Digital Media"), R("ep", 4, "Digital Media")], 12);

        Assert.Equal(["standard", "deluxe", "ep"], Ids(ranked));
    }

    // Ties must resolve the same way every run, or an album flips between pressings.
    [Fact]
    public void Breaks_track_count_ties_stably_by_id()
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
