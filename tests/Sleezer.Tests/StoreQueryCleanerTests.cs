using NzbDrone.Plugin.Sleezer.Core.Utilities;
using Xunit;

namespace Sleezer.Tests;

public class StoreQueryCleanerTests
{
    [Theory]
    [InlineData("Batman: Original Motion Picture Score", "Batman")]
    [InlineData("The Hack: Original Television Soundtrack", "The Hack")]
    [InlineData("Album (Deluxe Edition)", "Album")]
    [InlineData("Apollo (The Remixes)", "Apollo")]
    [InlineData("Discovery [Remastered]", "Discovery")]
    [InlineData("Title - 10th Anniversary Edition", "Title")]
    [InlineData("Plain Title", "Plain Title")]
    [InlineData("A / B", "A / B")]
    public void StripQualifiers_reduces_to_the_core_title(string title, string expected)
    {
        Assert.Equal(expected, StoreQueryCleaner.StripQualifiers(title));
    }

    [Theory]
    [InlineData("Deluxe")]     // an album literally named after a qualifier keeps its name
    [InlineData("(Remixes)")]
    public void StripQualifiers_keeps_the_original_when_nothing_would_remain(string title)
    {
        Assert.Equal(title, StoreQueryCleaner.StripQualifiers(title));
    }

    [Theory]
    [InlineData("Rock+Roll's  Here", "Rock Roll s Here")]
    [InlineData("  plain  ", "plain")]
    [InlineData("", "")]
    public void CleanForTokenSearch_normalises_plus_apostrophes_and_spacing(string query, string expected)
    {
        Assert.Equal(expected, StoreQueryCleaner.CleanForTokenSearch(query));
    }
}
