using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using Xunit;

namespace Sleezer.Tests;

// Issue #105: an artist with a 2018 EP and a 2022 album under one title had the EP's search
// grab the album — Lidarr re-parses an unresolvable release name using the search criteria,
// which assumes the result is whatever was searched.
public class AlbumYearGuardTests
{
    private static readonly NLog.Logger Log = NzbDroneLogger.GetLogger(typeof(AlbumYearGuardTests));

    private static ReleaseInfo R(string title, int year) => new()
    {
        Title = title,
        // Year 0 means "no date at all"; the parsers use UtcNow as their unknown sentinel.
        PublishDate = year == 0 ? default : new DateTime(year, 6, 1)
    };

    private static AlbumSearchCriteria C(int albumYear, bool interactive = false) => new()
    {
        AlbumTitle = "Shared Title",
        AlbumYear = albumYear,
        InteractiveSearch = interactive,
        Artist = new Artist { Name = "Test Artist" },
        Albums = []
    };

    private static string[] Titles(IList<ReleaseInfo> releases) => [.. releases.Select(r => r.Title)];

    [Fact]
    public void Drops_the_same_titled_album_from_another_year()
    {
        IList<ReleaseInfo> kept = AlbumYearGuard.Apply(
            [R("Shared Title (2022) [album]", 2022), R("Shared Title (2018) [ep]", 2018)], C(2018), "Qobuz", Log);

        Assert.Equal(["Shared Title (2018) [ep]"], Titles(kept));
    }

    // The store and MusicBrainz disagree by a year often enough that ±1 must survive.
    [Theory]
    [InlineData(2017)]
    [InlineData(2019)]
    public void Keeps_a_candidate_within_one_year_of_the_target(int candidateYear)
    {
        IList<ReleaseInfo> kept = AlbumYearGuard.Apply(
            [R("exact", 2018), R("near", candidateYear)], C(2018), "Qobuz", Log);

        Assert.Equal(2, kept.Count);
    }

    // The critical safety property: a catalogue whose years are uniformly shifted must be
    // left alone rather than emptied.
    [Fact]
    public void Does_nothing_when_no_candidate_matches_the_target_year()
    {
        ReleaseInfo[] all = [R("a", 2020), R("b", 2021), R("c", 2022)];

        Assert.Equal(3, AlbumYearGuard.Apply(all, C(2018), "Qobuz", Log).Count);
    }

    [Fact]
    public void Leaves_interactive_search_untouched()
    {
        IList<ReleaseInfo> kept = AlbumYearGuard.Apply(
            [R("wrong", 2022), R("right", 2018)], C(2018, interactive: true), "Qobuz", Log);

        Assert.Equal(2, kept.Count);
    }

    [Fact]
    public void Skips_when_the_album_has_no_known_year()
    {
        IList<ReleaseInfo> kept = AlbumYearGuard.Apply(
            [R("wrong", 2022), R("right", 2018)], C(0), "Qobuz", Log);

        Assert.Equal(2, kept.Count);
    }

    // Every parser stamps UtcNow when it has no usable date, and on the AlbumData-based
    // indexers (SubSonic, TripleTriple, slskd) that is most releases — a
    // year-precision date is deliberately discarded to avoid tripping EarlyReleaseSpecification.
    // Treating those as "this year" would drop correct results wholesale.
    private static ReleaseInfo Undated(string title) => new() { Title = title, PublishDate = DateTime.UtcNow };

    [Fact]
    public void An_undated_release_is_never_dropped()
    {
        IList<ReleaseInfo> kept = AlbumYearGuard.Apply(
            [R("right", 2018), Undated("no date"), R("wrong", 2022)], C(2018), "Slskd", Log);

        Assert.Equal(["right", "no date"], Titles(kept));
    }

    // ...and must not be the thing that makes the guard engage, or a search for a
    // current-year album would filter on a date nobody supplied.
    [Fact]
    public void An_undated_release_does_not_count_as_a_year_match()
    {
        int thisYear = DateTime.UtcNow.Year;
        ReleaseInfo[] all = [Undated("no date"), R("old", thisYear - 5)];

        Assert.Same(all, AlbumYearGuard.Apply(all, C(thisYear), "Slskd", Log));
    }

    // A scheduled release carries a real future date, not the parser's sentinel. Reading it
    // as undated would stop the guard engaging on an upcoming album entirely.
    [Fact]
    public void A_future_dated_release_is_real_data_not_the_sentinel()
    {
        int nextYear = DateTime.UtcNow.Year + 1;
        ReleaseInfo announced = new() { Title = "announced", PublishDate = new DateTime(nextYear, 6, 1, 0, 0, 0, DateTimeKind.Utc) };

        IList<ReleaseInfo> kept = AlbumYearGuard.Apply(
            [announced, R("old", nextYear - 5)], C(nextYear), "Qobuz", Log);

        Assert.Equal(["announced"], Titles(kept));
    }

    [Fact]
    public void Handles_no_results_and_no_criteria()
    {
        Assert.Empty(AlbumYearGuard.Apply([], C(2018), "Qobuz", Log));
        Assert.Single(AlbumYearGuard.Apply([R("x", 2018)], null, "Qobuz", Log));
    }

    // Every candidate matching means nothing is dropped and the original list comes back.
    [Fact]
    public void Returns_the_original_list_when_nothing_is_dropped()
    {
        ReleaseInfo[] all = [R("a", 2018), R("b", 2018)];

        Assert.Same(all, AlbumYearGuard.Apply(all, C(2018), "Qobuz", Log));
    }
}
