using NzbDrone.Core.Music;
using NzbDrone.Plugin.Sleezer.Core.PostProcessing;
using Xunit;

namespace Sleezer.Tests;

// Issue #102: 22 of 24 albums carrying a Qobuz SOURCE tag sat on a non-digital release
// while a Digital Media release existed. Lidarr ranks candidates by track-count distance
// alone, and CD pressings usually tie with the digital one, so the CD wins.
public class DigitalReleaseSelectorTests
{
    private static AlbumRelease R(string id, int trackCount, params string[] formats) => new()
    {
        ForeignReleaseId = id,
        Title = id,
        TrackCount = trackCount,
        Media = [.. formats.Select((f, i) => new Medium { Number = i + 1, Format = f })]
    };

    [Fact]
    public void Picks_the_digital_release_when_the_current_pick_is_a_cd()
    {
        AlbumRelease? chosen = DigitalReleaseSelector.Choose(
            [R("cd", 12, "CD"), R("digital", 12, "Digital Media")], R("cd", 12, "CD"), 12);

        Assert.Equal("digital", chosen?.ForeignReleaseId);
    }

    // The 12" Vinyl case named in the issue.
    [Fact]
    public void Picks_the_digital_release_over_vinyl()
    {
        AlbumRelease? chosen = DigitalReleaseSelector.Choose(
            [R("vinyl", 12, "12\" Vinyl"), R("digital", 12, "Digital Media")], R("vinyl", 12, "12\" Vinyl"), 12);

        Assert.Equal("digital", chosen?.ForeignReleaseId);
    }

    [Fact]
    public void Returns_null_when_the_current_pick_is_already_digital()
    {
        Assert.Null(DigitalReleaseSelector.Choose(
            [R("digital", 12, "Digital Media"), R("other", 12, "Digital Media")], R("digital", 12, "Digital Media"), 12));
    }

    [Fact]
    public void Returns_null_when_no_digital_release_exists()
    {
        Assert.Null(DigitalReleaseSelector.Choose([R("cd", 12, "CD"), R("vinyl", 12, "12\" Vinyl")], R("cd", 12, "CD"), 12));
    }

    // A multi-disc release is only digital if every medium is.
    [Fact]
    public void A_hybrid_release_does_not_count_as_digital()
    {
        Assert.False(DigitalReleaseSelector.IsDigital(R("hybrid", 20, "Digital Media", "CD")));
        Assert.Null(DigitalReleaseSelector.Choose([R("hybrid", 20, "Digital Media", "CD")], R("cd", 20, "CD"), 20));
    }

    [Fact]
    public void A_release_with_no_media_is_not_digital()
    {
        Assert.False(DigitalReleaseSelector.IsDigital(R("bare", 12)));
    }

    // Closest track count wins: the deluxe digital edition should not displace the
    // standard one when the folder holds exactly the standard track list.
    [Fact]
    public void Prefers_the_digital_release_closest_in_track_count()
    {
        AlbumRelease? chosen = DigitalReleaseSelector.Choose(
            [R("deluxe", 18, "Digital Media"), R("standard", 12, "Digital Media")], R("cd", 12, "CD"), 12);

        Assert.Equal("standard", chosen?.ForeignReleaseId);
    }

    // Ties must resolve the same way every run, or an album flips between pressings.
    [Fact]
    public void Breaks_track_count_ties_stably_by_id()
    {
        AlbumRelease[] candidates = [R("bbb", 12, "Digital Media"), R("aaa", 12, "Digital Media")];

        Assert.Equal("aaa", DigitalReleaseSelector.Choose(candidates, R("cd", 12, "CD"), 12)?.ForeignReleaseId);
        Assert.Equal("aaa", DigitalReleaseSelector.Choose(candidates.AsEnumerable().Reverse(), R("cd", 12, "CD"), 12)?.ForeignReleaseId);
    }

    [Fact]
    public void Never_returns_the_release_it_was_given_as_current()
    {
        Assert.Null(DigitalReleaseSelector.Choose([R("only", 12, "Digital Media")], R("only", 12, "Digital Media"), 12));
    }

    [Theory]
    [InlineData("digital media")]
    [InlineData("DIGITAL MEDIA")]
    public void Format_matching_is_case_insensitive(string format)
    {
        Assert.True(DigitalReleaseSelector.IsDigital(R("x", 12, format)));
    }

    [Fact]
    public void Handles_a_null_candidate_list_and_a_null_current_pick()
    {
        Assert.Null(DigitalReleaseSelector.Choose(null, null, 12));
        Assert.Equal("digital", DigitalReleaseSelector.Choose([R("digital", 12, "Digital Media")], null, 12)?.ForeignReleaseId);
    }
}
