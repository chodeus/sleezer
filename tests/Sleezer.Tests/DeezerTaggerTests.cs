using Newtonsoft.Json.Linq;
using NzbDrone.Core.Download.Clients.Deezer;
using Xunit;

namespace Sleezer.Tests;

public class DeezerTaggerTests
{
    [Fact]
    public void Writes_full_baseline_tags_from_gw_pages()
    {
        var trackPage = JObject.Parse("""
        {
          "DATA": {
            "SNG_ID": "123",
            "SNG_TITLE": "Angel",
            "VERSION": "(Extended Mix)",
            "ART_NAME": "Dimension",
            "ARTISTS": [ { "ART_NAME": "Dimension" }, { "ART_NAME": "Culture Shock" } ],
            "ALB_TITLE": "Organ",
            "TRACK_NUMBER": "5",
            "DISK_NUMBER": "2",
            "PHYSICAL_RELEASE_DATE": "2003-11-24"
          }
        }
        """);
        var albumPage = JObject.Parse("""
        {
          "DATA": {
            "ALB_TITLE": "Organ",
            "VERSION": "(Deluxe)",
            "ARTISTS": [ { "ART_NAME": "Dimension" } ]
          },
          "SONGS": {
            "total": 12,
            "data": [ { "DISK_NUMBER": "1" }, { "DISK_NUMBER": "2" } ]
          }
        }
        """);
        var art = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x01, 0x02 };

        var path = CopyFixture();
        try
        {
            DeezerTagger.Apply(path, trackPage, albumPage, art, "la la la");

            using var file = TagLib.File.Create(path);
            Assert.Equal("Angel (Extended Mix)", file.Tag.Title);
            Assert.Equal("Organ (Deluxe)", file.Tag.Album);
            Assert.Equal(new[] { "Dimension", "Culture Shock" }, file.Tag.Performers);
            Assert.Equal(new[] { "Dimension" }, file.Tag.AlbumArtists);
            Assert.Equal(2003u, file.Tag.Year);
            Assert.Equal(5u, file.Tag.Track);
            Assert.Equal(12u, file.Tag.TrackCount);
            Assert.Equal(2u, file.Tag.Disc);
            Assert.Equal(2u, file.Tag.DiscCount);
            Assert.Equal("la la la", file.Tag.Lyrics);
            Assert.Single(file.Tag.Pictures);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Missing_release_date_and_art_do_not_throw()
    {
        // DeezNET crashed on a missing PHYSICAL_RELEASE_DATE (culture-sensitive DateTime.Parse).
        var trackPage = JObject.Parse("""{ "DATA": { "SNG_ID": "1", "SNG_TITLE": "Angel", "ART_NAME": "Dimension", "ALB_TITLE": "Organ" } }""");
        var albumPage = JObject.Parse("""{ "DATA": { "ALB_TITLE": "Organ" }, "SONGS": { "total": 1, "data": [] } }""");

        var path = CopyFixture();
        try
        {
            DeezerTagger.Apply(path, trackPage, albumPage, albumArt: null, lyrics: string.Empty);

            using var file = TagLib.File.Create(path);
            Assert.Equal("Angel", file.Tag.Title);
            Assert.Equal(0u, file.Tag.Year);
            Assert.Empty(file.Tag.Pictures);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("2003-11-24", 2003u)]
    [InlineData("0000-00-00", 0u)]
    [InlineData("not-a-date", 0u)]
    [InlineData("", 0u)]
    public void Release_year_parses_invariantly(string physicalDate, uint expected)
    {
        var data = new JObject { ["PHYSICAL_RELEASE_DATE"] = physicalDate };

        Assert.Equal(expected, DeezerTagger.ReleaseYear(data, albumData: null));
    }

    [Fact]
    public void Release_year_falls_back_through_album_dates()
    {
        var data = new JObject();
        var albumData = new JObject { ["DIGITAL_RELEASE_DATE"] = "2010-01-15" };

        Assert.Equal(2010u, DeezerTagger.ReleaseYear(data, albumData));
    }

    private static string CopyFixture()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sleezer-tagger-{Guid.NewGuid():N}.flac");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "silence.flac"), path);
        return path;
    }
}
