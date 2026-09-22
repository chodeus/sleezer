using NLog;
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
        new SleezerParsingService(null!, artists, null!, null!, Log);

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
