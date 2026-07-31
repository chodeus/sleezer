using NzbDrone.Plugin.Sleezer.Core.PostProcessing;
using Xunit;

namespace Sleezer.Tests;

public class FeaturedArtistStripperTests
{
    [Theory]
    [InlineData("Song (feat. Other Artist)", "Song")]
    [InlineData("Song [Featuring Other Artist]", "Song")]
    [InlineData("Song {ft. Other Artist}", "Song")]
    [InlineData("Song (FEAT. Other Artist)", "Song")]
    [InlineData("Song (featuring Other)", "Song")]
    [InlineData("Song (ft Other)", "Song")]
    [InlineData("Song [feat Other]", "Song")]
    public void Strip_removes_bracketed_feat_suffixes(string input, string expected)
    {
        Assert.Equal(expected, FeaturedArtistStripper.Strip(input));
    }

    [Theory]
    [InlineData("My Featurette")]
    [InlineData("Feature Film")]
    [InlineData("Song feat. Other Artist")] // bare-text form intentionally not stripped
    [InlineData("Song featuring Other")]
    [InlineData("Song ft Other")]
    public void Strip_leaves_non_bracketed_feat_text_alone(string input)
    {
        Assert.Equal(input, FeaturedArtistStripper.Strip(input));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    public void Strip_handles_null_and_empty(string? input, string expected)
    {
        Assert.Equal(expected, FeaturedArtistStripper.Strip(input));
    }

    [Fact]
    public void Strip_preserves_inner_content_after_removing_feat()
    {
        Assert.Equal("Song Name", FeaturedArtistStripper.Strip("Song Name (feat. Someone Else)"));
    }

    [Fact]
    public void Strip_handles_multiple_feat_suffixes()
    {
        // If a title somehow has two bracketed feat-clauses, both should go.
        Assert.Equal("Song", FeaturedArtistStripper.Strip("Song (feat. A) [ft. B]"));
    }

    // Live 2026-07-31: comma-joined credits dragged the artist's own single
    // below Lidarr's import cutoff ("T & Sugah, Grace Barton" → 77.9% vs 80%).
    [Theory]
    [InlineData("T & Sugah, Grace Barton", "T & Sugah", "T & Sugah")]
    [InlineData("A$AP Rocky, Brent Faiyaz", "A$AP Rocky", "A$AP Rocky")]
    [InlineData("t & sugah, Grace Barton", "T & Sugah", "t & sugah")]              // case-insensitive anchor, input casing kept
    [InlineData("T & Sugah; Grace Barton", "T & Sugah", "T & Sugah")]
    [InlineData("T & Sugah feat. Grace Barton", "T & Sugah", "T & Sugah")]         // bare feat is safe once anchored
    [InlineData("T & Sugah ft Grace Barton", "T & Sugah", "T & Sugah")]
    public void StripGuestCredits_strips_when_anchored_on_primary_artist(string input, string artist, string expected)
    {
        Assert.Equal(expected, FeaturedArtistStripper.StripGuestCredits(input, artist));
    }

    [Theory]
    [InlineData("Grace Barton, T & Sugah", "T & Sugah")]       // artist not the prefix
    [InlineData("T & Sugarman, Grace", "T & Sugah")]           // prefix must be the full artist name
    [InlineData("T & Sugah & Grace Barton", "T & Sugah")]      // '&' join is ambiguous with duo names — left alone
    [InlineData("T & Sugah", "T & Sugah")]                     // exact match, nothing to strip
    [InlineData("Various Artists", "T & Sugah")]
    public void StripGuestCredits_leaves_unanchored_values_alone(string input, string artist)
    {
        Assert.Equal(input, FeaturedArtistStripper.StripGuestCredits(input, artist));
    }

    [Theory]
    [InlineData(null, "T & Sugah", null)]
    [InlineData("T & Sugah, Grace", null, "T & Sugah, Grace")]
    [InlineData("", "", "")]
    public void StripGuestCredits_passes_null_and_empty_through(string? input, string? artist, string? expected)
    {
        Assert.Equal(expected, FeaturedArtistStripper.StripGuestCredits(input, artist));
    }
}
