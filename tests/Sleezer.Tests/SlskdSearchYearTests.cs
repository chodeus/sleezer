using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Music;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek.Search.Core;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek.Search.Strategies;
using Xunit;

namespace Sleezer.Tests;

// An album Lidarr dates 0001-01-01 searches with AlbumYear 1; a self-titled one became "Artist 1".
public class SlskdSearchYearTests
{
    private static SearchContext WithYear(string? year, string templates = "") =>
        new("Artist", "Album", year, Interactive: false, TrackCount: 0, Aliases: [], Tracks: [],
            new SlskdSettings { SearchTemplates = templates }, ProcessedSearches: [],
            new AlbumSearchCriteria { Artist = new Artist { Name = "Artist" }, AlbumTitle = "Album", AlbumYear = int.TryParse(year, out int y) ? y : 0 });

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("1", false)]
    [InlineData("2019", true)]
    public void Only_a_known_year_goes_into_queries(string? year, bool expected) =>
        Assert.Equal(expected, WithYear(year).HasValidYear);

    [Theory]
    [InlineData("1", "Artist")]
    [InlineData("2019", "Artist 2019")]
    public void A_self_titled_query_carries_only_a_known_year(string year, string expected) =>
        Assert.Equal(expected, new SelfTitledStrategy().GetQuery(WithYear(year), QueryType.SelfTitled));

    [Theory]
    [InlineData("1", "Artist Album")]
    [InlineData("2019", "Artist 2019")]
    public void A_template_that_needs_an_unknown_year_is_skipped(string year, string expected) =>
        Assert.Equal(expected, new TemplateSearchStrategy().GetQuery(
            WithYear(year, "{{ArtistQuery}} {{ albumyear }}\n{{ArtistQuery}} {{AlbumQuery}}"), QueryType.Normal));
}
