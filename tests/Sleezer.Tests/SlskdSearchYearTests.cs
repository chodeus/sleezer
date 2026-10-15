using NzbDrone.Plugin.Sleezer.Indexers.Soulseek;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek.Search.Core;
using Xunit;

namespace Sleezer.Tests;

public class SlskdSearchYearTests
{
    private static SearchContext WithYear(string? year) =>
        new("Artist", "Album", year, Interactive: false, TrackCount: 0, Aliases: [], Tracks: [], new SlskdSettings(), ProcessedSearches: []);

    // An album Lidarr dates 0001-01-01 searches with year 1; a self-titled one became "Artist 1".
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("1", false)]
    [InlineData("2019", true)]
    public void Only_a_known_year_goes_into_queries(string? year, bool expected) =>
        Assert.Equal(expected, WithYear(year).HasValidYear);
}
