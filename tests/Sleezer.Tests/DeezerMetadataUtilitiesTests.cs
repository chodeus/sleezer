using Newtonsoft.Json.Linq;
using NzbDrone.Core.Download.Clients.Deezer;
using Xunit;

namespace Sleezer.Tests;

// Deezer used to sanitize with a bare Path.GetInvalidFileNameChars() loop, which on
// Linux covers only '/' and NUL — so ':' and friends reached the filesystem. These
// pin the delegation to the canonical sanitizer.
public class DeezerMetadataUtilitiesTests
{
    [Theory]
    [InlineData("Live at Wembley: The Return", "Live at Wembley_ The Return")]
    [InlineData("AC\\DC", "AC_DC")]
    [InlineData("What?", "What_")]
    [InlineData("A*B", "A_B")]
    [InlineData("<tag>", "_tag_")]
    [InlineData("pipe|name", "pipe_name")]
    [InlineData("quote\"name", "quote_name")]
    [InlineData("slash/inside", "slash_inside")]
    public void CleanPath_strips_characters_linux_would_otherwise_allow(string input, string expected)
    {
        Assert.Equal(expected, MetadataUtilities.CleanPath(input));
    }

    [Theory]
    [InlineData("Trailing dot.", "Trailing dot")]
    [InlineData("Trailing space ", "Trailing space")]
    public void CleanPath_trims_trailing_dots_and_spaces(string input, string expected)
    {
        Assert.Equal(expected, MetadataUtilities.CleanPath(input));
    }

    [Theory]
    [InlineData("Niño Bonito")]
    [InlineData("赤いスイートピー")]
    [InlineData("Perfectly Ordinary Album")]
    public void CleanPath_passes_safe_titles_through(string input)
    {
        Assert.Equal(input, MetadataUtilities.CleanPath(input));
    }

    private static JObject Page(string artist, string album, string releaseDate) => JObject.Parse($$"""
        {
          "DATA": {
            "SNG_TITLE": "Song", "ALB_TITLE": {{Json(album)}}, "ART_NAME": {{Json(artist)}},
            "ARTISTS": [ { "ART_NAME": {{Json(artist)}} } ], "TRACK_NUMBER": 1,
            "PHYSICAL_RELEASE_DATE": {{Json(releaseDate)}}
          },
          "SONGS": { "total": 1 }
        }
        """);

    private static string Json(string value) => Newtonsoft.Json.JsonConvert.ToString(value);

    [Theory]
    [InlineData("0000-00-00")]
    [InlineData("")]
    public void An_unknown_release_date_still_names_the_track(string releaseDate)
    {
        JObject page = Page("Artist", "Album", releaseDate);

        Assert.Equal("Artist/Album/01 - Song.flac", MetadataUtilities.GetFilledTemplate("%albumartist%/%album%/%track% - %title%.%ext%", "flac", page, page));
    }

    [Theory]
    [InlineData("...")]
    [InlineData("")]
    public void A_name_that_cleans_to_nothing_never_roots_the_folder(string artist)
    {
        JObject page = Page(artist, "Album", "2020-01-01");

        string folder = MetadataUtilities.GetFilledTemplate("%albumartist%/%album%/", "flac", page, page);

        Assert.Equal("_/Album/", folder);
        Assert.False(Path.IsPathRooted(folder));
    }
}
