using NLog;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Core.Replacements;
using Sleezer.Tests.Fakes;
using Xunit;

namespace Sleezer.Tests;

// Lidarr's download-history fallbacks only run when the name lookup returns instead of throwing.
public class SleezerParsingServiceTests
{
    private static readonly Logger Log = LogManager.GetLogger(nameof(SleezerParsingServiceTests));

    // AlbumTitle stays null so GetAlbums returns early and never reaches IAlbumService.
    private static ParsedAlbumInfo Parsed(string artistName) => new() { ArtistName = artistName };

    private static IParsingService Subject(IArtistService artists) =>
        new SleezerParsingService(new ParsingService(null!, artists, null!, null!, Log), Log);

    [Fact]
    public void GetArtist_returns_null_when_the_clean_name_is_shared()
    {
        Assert.Null(Subject(new CollidingArtistService()).GetArtist("Some Artist - Some Album"));
    }

    [Fact]
    public void Map_returns_an_artistless_album_when_the_clean_name_is_shared()
    {
        ParsedAlbumInfo parsed = Parsed("Some Artist");

        RemoteAlbum mapped = Subject(new CollidingArtistService()).Map(parsed);

        Assert.Null(mapped.Artist);
        Assert.Same(parsed, mapped.ParsedAlbumInfo);
    }

    // Control: without the override the same input throws, so the two tests above are not vacuous.
    [Fact]
    public void Base_ParsingService_throws_on_the_same_input()
    {
        IParsingService unpatched = new ParsingService(null!, new CollidingArtistService(), null!, null!, Log);

        Assert.Throws<MultipleArtistsFoundException>(() => unpatched.GetArtist("Some Artist - Some Album"));
        Assert.Throws<MultipleArtistsFoundException>(() => unpatched.Map(Parsed("Some Artist")));
    }

    // ParseMusicPath is pure string work, so no fixture file is needed to reach FindByName.
    [Fact]
    public void GetArtistFromTag_returns_null_when_the_clean_name_is_shared()
    {
        Assert.Null(Subject(new CollidingArtistService())
            .GetArtistFromTag("/music/Some Artist/Some Artist - Some Album - 01 - Some Track.flac"));
    }

    // The override must not swallow the unambiguous path it also sits on.
    [Fact]
    public void An_unambiguous_name_still_resolves()
    {
        Artist only = new() { Name = "Some Artist" };

        Assert.Same(only, Subject(new SingleArtistService(only)).GetArtist("Some Artist - Some Album"));
        Assert.Same(only, Subject(new SingleArtistService(only)).Map(Parsed("Some Artist")).Artist);
    }

    private sealed class CollidingArtistService : FakeArtistService
    {
        public override Artist FindByName(string title) => throw new MultipleArtistsFoundException(
            [new Artist { Name = "Some Artist" }, new Artist { Name = "Some  Artist" }],
            "Expected one artist, but found {0}. Matching artists: {1}", 2, title);
    }

    private sealed class SingleArtistService(Artist artist) : FakeArtistService
    {
        public override Artist FindByName(string title) => artist;
    }
}

// FindByTitle gives up on a clean title two library albums share; a search means the searched one.
public class PreferSearchedAlbumTests
{
    private static readonly Artist Searched = new() { Id = 1, Name = "Some Artist", CleanName = "someartist" };

    private static RemoteAlbum Mapped(string parsedTitle, Artist? artist, params Album[] albums) =>
        new() { ParsedAlbumInfo = new ParsedAlbumInfo { AlbumTitle = parsedTitle, ArtistName = "Some Artist" }, Artist = artist, Albums = [.. albums] };

    private static AlbumSearchCriteria Criteria(params Album[] albums) => new() { Artist = Searched, Albums = [.. albums] };

    private static Album A(int id, string title) => new() { Id = id, Title = title };

    // Through Map with Lidarr's own ParsingService: its exact-title step misses a braced title and the
    // library lookups come back empty, as they do when two albums share the clean title.
    [Fact]
    public void Map_sends_a_title_Lidarr_could_not_place_to_the_searched_album()
    {
        Album searched = A(7, "In and Out of Love (Remixes EP)");
        var parsing = new SleezerParsingService(new ParsingService(null!, null!, new Sleezer.Tests.Fakes.FakeAlbumService([], titleLookupsFindNothing: true), null!, LogManager.CreateNullLogger()), LogManager.CreateNullLogger());

        RemoteAlbum result = parsing.Map(new ParsedAlbumInfo { ArtistName = "Some Artist", AlbumTitle = "In and Out of Love {Remixes EP" }, Criteria(searched));

        Assert.Same(Searched, result.Artist);
        Assert.Same(searched, Assert.Single(result.Albums));
    }

    [Fact]
    public void An_unmapped_title_goes_to_the_searched_album_with_that_clean_title()
    {
        Album searched = A(7, "In and Out of Love (Remixes EP)");

        RemoteAlbum result = SleezerParsingService.PreferSearchedAlbum(Mapped("In and Out of Love {Remixes EP", Searched), Criteria(searched));

        Assert.Same(searched, Assert.Single(result.Albums));
    }

    [Fact]
    public void A_title_mapped_to_another_album_goes_to_the_searched_one()
    {
        Album searched = A(7, "Stateside");

        RemoteAlbum result = SleezerParsingService.PreferSearchedAlbum(Mapped("Stateside", Searched, A(3, "Stateside")), Criteria(searched));

        Assert.Same(searched, Assert.Single(result.Albums));
    }

    [Fact]
    public void Another_artists_result_is_left_alone()
    {
        Album other = A(3, "Stateside");
        Artist someoneElse = new() { Id = 2, Name = "Someone Else", CleanName = "someoneelse" };

        RemoteAlbum result = SleezerParsingService.PreferSearchedAlbum(Mapped("Stateside", someoneElse, other), Criteria(A(7, "Stateside")));

        Assert.Same(other, Assert.Single(result.Albums));
    }

    [Fact]
    public void Two_searched_albums_with_one_clean_title_are_left_to_Lidarr()
    {
        RemoteAlbum result = SleezerParsingService.PreferSearchedAlbum(Mapped("Stateside", Searched), Criteria(A(7, "Stateside"), A(8, "Stateside")));

        Assert.Empty(result.Albums);
    }

    [Fact]
    public void A_title_that_matches_no_searched_album_is_left_alone()
    {
        Album mapped = A(3, "In and Out of Love");

        RemoteAlbum result = SleezerParsingService.PreferSearchedAlbum(Mapped("In and Out of Love", Searched, mapped), Criteria(A(7, "In and Out of Love (Remixes EP)")));

        Assert.Same(mapped, Assert.Single(result.Albums));
    }
}
