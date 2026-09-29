using NzbDrone.Core.Music;
using NzbDrone.Plugin.Sleezer.Core.PostProcessing;
using Xunit;

namespace Sleezer.Tests;

// Live 2026-08-26: no Digital Media release existed yet, so the fallback wrote an 8-track
// CD-R's identity onto a 7-track Qobuz single; Lidarr scored the right release at 39.5%.
public class PreImportTaggerFallbackGuardTests
{
    private static AlbumRelease R(int trackCount, params string[] formats) => new()
    {
        ForeignReleaseId = "release",
        Title = "release",
        TrackCount = trackCount,
        Media = [.. formats.Select((f, i) => new Medium { Number = i + 1, Format = f })]
    };

    [Fact]
    public void Rejects_a_physical_release_for_a_storefront_download()
    {
        Assert.False(TitleFallbackGuard.IsSafeTarget(R(7, "CD-R"), 7, preferDigitalMedia: true));
    }

    [Fact]
    public void Accepts_a_digital_release_for_a_storefront_download()
    {
        Assert.True(TitleFallbackGuard.IsSafeTarget(R(7, "Digital Media"), 7, preferDigitalMedia: true));
    }

    // Scoped to digital sources — slskd and friends still tag off physical releases.
    [Fact]
    public void Accepts_a_physical_release_when_the_source_is_not_a_storefront()
    {
        Assert.True(TitleFallbackGuard.IsSafeTarget(R(7, "CD-R"), 7, preferDigitalMedia: false));
    }

    [Theory]
    [InlineData(8, 7)]
    [InlineData(7, 8)]
    public void Rejects_a_release_of_a_different_length(int releaseTrackCount, int localTrackCount)
    {
        Assert.False(TitleFallbackGuard.IsSafeTarget(
            R(releaseTrackCount, "Digital Media"), localTrackCount, preferDigitalMedia: true));
    }

    [Fact]
    public void Rejects_a_release_of_a_different_length_for_any_source()
    {
        Assert.False(TitleFallbackGuard.IsSafeTarget(R(8, "CD"), 7, preferDigitalMedia: false));
    }

    [Fact]
    public void Rejects_a_missing_release()
    {
        Assert.False(TitleFallbackGuard.IsSafeTarget(null, 7, preferDigitalMedia: false));
    }

    // No media at all is not digital — an unknown format must not read as "not physical".
    [Fact]
    public void Rejects_a_release_with_no_media_for_a_storefront_download()
    {
        Assert.False(TitleFallbackGuard.IsSafeTarget(R(7), 7, preferDigitalMedia: true));
    }

    private static Album A(string type, params SecondaryAlbumType[] secondary) => new() { AlbumType = type, SecondaryTypes = [.. secondary] };

    [Theory]
    [InlineData("Song (Remixer remix)")]
    [InlineData("Song (remix edit)", "Song (remix)")]
    public void A_remix_single_whose_tracks_name_the_remix_is_eligible(params string[] tracks)
    {
        Assert.True(TitleFallbackGuard.IsEligibleAlbum(A("Single", SecondaryAlbumType.Remix), tracks));
    }

    [Theory]
    [InlineData("Song")]
    [InlineData("Song", "Song (Remixer remix)")]
    public void A_remix_single_with_a_plain_track_title_stays_ineligible(params string[] tracks)
    {
        Assert.False(TitleFallbackGuard.IsEligibleAlbum(A("Single", SecondaryAlbumType.Remix), tracks));
    }

    [Fact]
    public void A_remix_single_with_no_tracks_stays_ineligible()
    {
        Assert.False(TitleFallbackGuard.IsEligibleAlbum(A("Single", SecondaryAlbumType.Remix), []));
    }

    [Fact]
    public void A_live_single_stays_ineligible_even_when_its_titles_say_live()
    {
        Assert.False(TitleFallbackGuard.IsEligibleAlbum(A("Single", SecondaryAlbumType.Live), ["Song (live)"]));
    }

    [Theory]
    [InlineData("Single", true)]
    [InlineData("EP", true)]
    [InlineData("Album", false)]
    public void Only_small_releases_are_eligible(string type, bool eligible)
    {
        Assert.Equal(eligible, TitleFallbackGuard.IsEligibleAlbum(A(type), ["Song"]));
    }

    // Eligibility leans on the matcher refusing a plain file for a named remix.
    [Fact]
    public void On_an_eligible_remix_single_only_the_remix_file_matches()
    {
        Dictionary<int, int> mapping = TrackTitleMatcher.Match(
            ["Song (feat. Guest)", "Song (feat. Guest) (Other remix)", "Song (feat. Guest) (Remixer Remix)"],
            ["Song (Remixer remix)"]);

        Assert.Equal(new Dictionary<int, int> { [2] = 0 }, mapping);
    }
}
