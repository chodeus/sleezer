using Xunit;
using static NzbDrone.Plugin.Sleezer.Core.PostProcessing.TrackTitleMatcher;

namespace Sleezer.Tests;

// A file title inside a longer wanted title passed the fuzzy gate: "Fire" matched "Line of Fire", and a shared
// "(extended mix)" carried "Glass" onto "Moon Over Glass (extended mix)".
public class TrackTitleMatcherGateTests
{
    [Theory]
    [InlineData("Fire", "Line of Fire")]
    [InlineData("Glass (extended mix)", "Moon Over Glass (extended mix)")]
    [InlineData("Lantern Song", "The Lantern Song Returns")]
    public void Two_words_of_the_wanted_title_missing_name_another_song(string local, string wanted)
    {
        Assert.Empty(Match([local], [wanted]));
    }

    [Theory]
    [InlineData("Lantern Song", "Lantern Song (album version)")]
    [InlineData("Lantern Song", "Lantern Song (from the Glass Harbour soundtrack)")]
    [InlineData("Moss Remix", "Lantern Song - Moss Remix")]
    [InlineData("Moss Remix", "Lantern Song – Moss Remix")]
    [InlineData("Lantern Song, Pt. 2", "Lantern Song, Part II")]
    [InlineData("Lantern", "Lantern Song")]
    [InlineData("Lanternsong", "Lantern-Song")]
    [InlineData("Ballad of Glass", "The Ballad of the Glass")]
    [InlineData("Lantern Song", "Lantern Song - Single Version")]
    [InlineData("Lantern Song", "Lantern Song\uFF08TV Size\uFF09")]
    public void A_title_the_file_carries_in_full_still_matches(string local, string wanted)
    {
        Assert.Equal(0, Assert.Single(Match([local], [wanted])).Value);
    }
}
