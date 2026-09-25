using NLog;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Core.Model;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using Xunit;
using LidarrParser = NzbDrone.Core.Parser.Parser;

namespace Sleezer.Tests;

// Lidarr maps a result by re-parsing its title, and rejects "Wrong album" unless the parsed album's
// clean title equals the searched one (SingleAlbumSearchMatchSpecification).
public class ReleaseTitleTests
{
    private const string Tail = " (2026) [FLAC] [WEB]";
    private static readonly Logger Log = LogManager.CreateNullLogger();

    private static StoreReleaseInfo Composed(string artist, string album, string? candidate = null)
    {
        StoreReleaseInfo release = new() { Artist = artist, Album = album, CandidateTitle = candidate ?? album };
        ReleaseTitle.Compose(release, artist, album, Tail);
        return release;
    }

    private static string ParsedClean(ReleaseInfo release) => LidarrParser.ParseAlbumTitle(release.Title)!.AlbumTitle.CleanArtistName();

    private static string ParsedArtistClean(ReleaseInfo release) => LidarrParser.ParseAlbumTitle(release.Title)!.ArtistName.CleanArtistName();

    [Theory]
    [InlineData("In and Out of Love (Remixes EP)")]
    [InlineData("Stateside")]
    [InlineData("Song (feat. Guest)")]
    [InlineData("Album [Deluxe Edition] (Remastered)")]
    [InlineData("Live (at the Apollo")]
    [InlineData("Songs (Best Of)")]
    [InlineData("Something (Side A)")]
    public void A_searched_title_parses_back_to_the_searched_album(string searched)
    {
        StoreReleaseInfo release = Composed("Some Artist", "Store Wording");

        Assert.True(ReleaseTitle.AsSearchedAlbum(release, "Some Artist", searched));

        Assert.Equal(searched.CleanArtistName(), ParsedClean(release));
    }

    // Bandcamp has no year: the album ends at the first "[" instead.
    [Fact]
    public void A_title_without_a_year_parses_back_too()
    {
        StoreReleaseInfo release = new() { Artist = "Some Artist", Album = "In And Out Of Love (Remixes)" };
        ReleaseTitle.Compose(release, "Some Artist", "In And Out Of Love (Remixes)", " [3 tracks] [WEB] [FLAC]");

        ReleaseTitle.AsSearchedAlbum(release, "Some Artist", "In and Out of Love (Remixes EP)");

        Assert.Equal("In and Out of Love (Remixes EP)".CleanArtistName(), ParsedClean(release));
    }

    [Theory]
    [InlineData("In And Out Of Love (Remixes)", "In And Out Of Love Remixes")]
    [InlineData("Song (feat. Guest)", "Song")]
    [InlineData("Song feat. Guest", "Song")]
    [InlineData("Song (feat. Guest) (Club Mix)", "Song Club Mix")]
    [InlineData("Album [Deluxe]", "Album Deluxe")]
    [InlineData("Album (Deluxe Edition) feat. Guest", "Album Deluxe Edition")]
    [InlineData("Album (Remix feat. Guest)", "Album Remix feat. Guest")]
    [InlineData("Little Feat Live", "Little Feat Live")]
    [InlineData("Live at Ft Worth", "Live at Ft Worth")]
    public void A_store_title_keeps_its_brackets_but_drops_a_featured_credit(string store, string expected)
    {
        Assert.Equal(expected.CleanArtistName(), ParsedClean(Composed("Some Artist", store)));
    }

    // The store's own wording stays after the year, where custom formats and the UI still see it.
    [Fact]
    public void Presenting_as_searched_keeps_the_tags_and_the_store_wording()
    {
        StoreReleaseInfo release = Composed("Some Artist", "In And Out Of Love (Remixes)");

        ReleaseTitle.AsSearchedAlbum(release, "Some Artist", "In and Out of Love (Remixes EP)");

        Assert.Equal("Some Artist - In and Out of Love {Remixes EP}" + Tail + " [Some Artist - In And Out Of Love (Remixes)]", release.Title);
    }

    [Fact]
    public void A_release_without_composed_parts_is_left_alone()
    {
        StoreReleaseInfo release = new() { Title = "Some Artist - Album" + Tail };

        Assert.False(ReleaseTitle.AsSearchedAlbum(release, "Some Artist", "Album (Remixes)"));
        Assert.Equal("Some Artist - Album" + Tail, release.Title);
    }

    private static AlbumSearchCriteria Criteria(string albumTitle, string artistName = "Some Artist", params string[] aliases)
    {
        Artist artist = new() { Name = artistName, CleanName = artistName.CleanArtistName() };
        artist.Metadata = new LazyLoaded<ArtistMetadata>(new ArtistMetadata { Name = artistName, Aliases = [.. aliases] });
        return new AlbumSearchCriteria
        {
            Artist = artist,
            AlbumTitle = albumTitle,
            Albums = [new Album { Title = albumTitle, SecondaryTypes = [], AlbumReleases = new LazyLoaded<List<AlbumRelease>>([]) }],
        };
    }

    private static IList<ReleaseInfo> Refine(ReleaseInfo release, string searched, bool strict = true) =>
        StoreResultRefiner.Refine([release], Criteria(searched), strict, [], "Store", Log);

    private static IList<ReleaseInfo> Refine(ReleaseInfo release, AlbumSearchCriteria criteria) =>
        StoreResultRefiner.Refine([release], criteria, true, [], "Store", Log);

    [Fact]
    public void A_verified_result_is_presented_as_the_searched_album()
    {
        StoreReleaseInfo release = Composed("Some Artist", "In And Out Of Love (Remixes)");

        Refine(release, "In and Out of Love (Remixes EP)");

        Assert.Null(release.Rejection);
        Assert.Equal("In and Out of Love (Remixes EP)".CleanArtistName(), ParsedClean(release));
    }

    [Fact]
    public void A_result_that_failed_verification_keeps_its_own_title()
    {
        StoreReleaseInfo release = Composed("Some Artist", "Something Else Entirely");

        Refine(release, "In and Out of Love (Remixes EP)");

        Assert.NotNull(release.Rejection);
        Assert.Equal("Something Else Entirely".CleanArtistName(), ParsedClean(release));
    }

    // The verifier passes a title it cannot judge; that is not a match.
    [Fact]
    public void A_result_whose_title_could_not_be_judged_keeps_its_own_title()
    {
        StoreReleaseInfo release = Composed("Some Artist", "...", candidate: "...");
        string before = release.Title;

        Refine(release, "Stateside");

        Assert.Null(release.Rejection);
        Assert.Equal(before, release.Title);
    }

    // Renamed, a result skips Lidarr's exact title check, so a fuzzy match is not enough.
    [Theory]
    [InlineData("Greatest Hits Vol. 2", "Greatest Hits Vol. 1")]
    [InlineData("Symphony (Part II)", "Symphony (Part I)")]
    [InlineData("Album (Volume 2) (Disc 1)", "Album (Volume 1) (Disc 2)")]
    public void A_different_number_is_never_presented_as_the_searched_album(string store, string searched)
    {
        StoreReleaseInfo release = Composed("Some Artist", store);

        Refine(release, searched);

        // Passed verification, so only the exact-title rule kept it from being renamed.
        Assert.Null(release.Rejection);
        Assert.NotEqual(searched.CleanArtistName(), ParsedClean(release));
    }

    [Fact]
    public void A_remaster_year_does_not_count_as_a_different_album()
    {
        StoreReleaseInfo release = Composed("Some Artist", "Album (2011 Remaster)");

        Refine(release, "Album");

        Assert.Equal("Album".CleanArtistName(), ParsedClean(release));
    }

    // Lidarr's artist lookup fails on a collaboration credit, and braces defeat its search-criteria reparse.
    // Qobuz lists each main artist, which is what names the searched one outright.
    [Fact]
    public void A_verified_collaboration_credit_is_presented_as_the_searched_artist()
    {
        StoreReleaseInfo release = Composed("Some Artist & Guest", "In And Out Of Love (Remixes)");
        release.MainArtists = ["Some Artist", "Guest"];

        Refine(release, "In and Out of Love (Remixes EP)");

        Assert.Equal("Some Artist".CleanArtistName(), ParsedArtistClean(release));
        Assert.Contains("[Some Artist & Guest - In And Out Of Love (Remixes)]", release.Title);
    }

    // Renamed, the result skips Lidarr's artist lookup, so the verifier's lenient artist match is not enough.
    [Fact]
    public void An_artist_whose_name_only_contains_the_searched_one_keeps_its_own_credit()
    {
        StoreReleaseInfo release = Composed("Low Roar", "Live");

        Refine(release, Criteria("Live", "Low"));

        Assert.Null(release.Rejection);
        Assert.Equal("Low Roar".CleanArtistName(), ParsedArtistClean(release));
    }

    // Credits are never split, so a duo is not taken for one of its members without a main-artist list.
    [Fact]
    public void A_collaboration_credit_without_listed_main_artists_keeps_its_own_credit()
    {
        StoreReleaseInfo release = Composed("Some Artist & Guest", "Live");

        Refine(release, Criteria("Live", "Some Artist"));

        Assert.Null(release.Rejection);
        Assert.Equal("Some Artist & Guest".CleanArtistName(), ParsedArtistClean(release));
    }

    [Fact]
    public void A_credit_under_an_alias_is_presented_as_the_searched_artist()
    {
        StoreReleaseInfo release = Composed("SA", "In And Out Of Love (Remixes)");

        Refine(release, Criteria("In and Out of Love (Remixes EP)", "Some Artist", "SA"));

        Assert.Equal("Some Artist".CleanArtistName(), ParsedArtistClean(release));
    }

    [Fact]
    public void Nothing_is_retitled_when_strict_matching_is_off()
    {
        StoreReleaseInfo release = Composed("Some Artist", "In And Out Of Love (Remixes)");
        string before = release.Title;

        Refine(release, "In and Out of Love (Remixes EP)", strict: false);

        Assert.Equal(before, release.Title);
    }

    // Slskd names a matched folder after the searched title, whose brackets are part of it.
    [Theory]
    [InlineData(true, "Song (feat. Guest)")]
    [InlineData(false, "Song")]
    public void A_slskd_album_named_after_the_search_keeps_its_featured_credit(bool searchedTitle, string expected)
    {
        AlbumData album = new("Slskd", "SoulseekDownloadProtocol")
        {
            ArtistName = "Some Artist",
            AlbumName = "Song (feat. Guest)",
            AlbumIsSearchedTitle = searchedTitle,
            Codec = AudioFormat.FLAC,
        };

        Assert.Equal(expected.CleanArtistName(), ParsedClean(album.ToShareInfo()));
    }
}
