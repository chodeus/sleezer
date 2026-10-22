using Xunit;
using static NzbDrone.Plugin.Sleezer.Core.PostProcessing.TrackTitleMatcher;

namespace Sleezer.Tests;

public class TrackTitleMatcherGateTests
{
    [Theory]
    [InlineData("Fire", "Line of Fire")]
    [InlineData("Glass (extended mix)", "Moon Over Glass (extended mix)")]
    [InlineData("Lantern Song", "The Lantern Song Returns")]
    [InlineData("Time", "One More Time")]
    [InlineData("Glass", "Two Glass Lanterns")]
    [InlineData("Me", "Take Me Home")]
    [InlineData("Lanterns", "Book of Lanterns")]
    public void Two_words_of_the_wanted_title_missing_name_another_song(string local, string wanted)
    {
        Assert.Empty(Match([local], [wanted]));
    }

    [Theory]
    [InlineData("Lantern Song", "Lantern Song (album version)")]
    [InlineData("Lantern Song", "Lantern Song (from the Glass Harbour soundtrack)")]
    [InlineData("Lantern Song", "Lantern Song (from (Glass Harbour) soundtrack)")]
    [InlineData("Moss Remix", "Lantern Song - Moss Remix")]
    [InlineData("Moss Remix", "Lantern Song – Moss Remix")]
    [InlineData("Lantern Song", "Lantern Song (album version) - The Glass Harbour")]
    [InlineData("Moss Remix (Extended)", "Lantern Song - Moss Remix (Extended)")]
    [InlineData("Lantern Song", "The Glass Harbour - Lantern Song (album version)")]
    [InlineData("Lantern Song, Pt. 2", "Lantern Song, Part II")]
    [InlineData("Lantern Song Volume 2 Part 3", "Lantern Song Volume Two Part Three")]
    [InlineData("Lantern Song 2 3", "Lantern Song Volume 2 Part 3")]
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
