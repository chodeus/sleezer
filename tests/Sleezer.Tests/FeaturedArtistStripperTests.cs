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
}
