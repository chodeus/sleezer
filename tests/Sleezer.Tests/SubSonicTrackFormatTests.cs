using System.Text.Json;
using NzbDrone.Plugin.Sleezer.Core.Model;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using NzbDrone.Plugin.Sleezer.Download.Clients.SubSonic;
using NzbDrone.Plugin.Sleezer.Indexers.SubSonic;
using Xunit;

namespace Sleezer.Tests;

// SubSonic tracks were all saved as .flac, whatever the server streamed (Tubifarry 7e4458f).
public class SubSonicTrackFormatTests
{
    private static SubSonicSearchSong Song(string? suffix = null, string? contentType = null, string? transcodedSuffix = null, string? transcodedContentType = null) =>
        new("1", "t", "a", Suffix: suffix, ContentType: contentType, TranscodedSuffix: transcodedSuffix, TranscodedContentType: transcodedContentType);

    [Theory]
    [InlineData("flac", null, "mp3", null, PreferredFormatEnum.Raw, ".mp3")]
    [InlineData("flac", null, null, "audio/ogg", PreferredFormatEnum.Raw, ".ogg")]
    [InlineData("flac", null, null, null, PreferredFormatEnum.Opus, ".opus")]
    [InlineData("flac", null, "mp3", "audio/mpeg", PreferredFormatEnum.Opus, ".opus")]
    [InlineData("flac", null, null, null, PreferredFormatEnum.Aac, ".aac")]
    [InlineData("MP3", null, null, null, PreferredFormatEnum.Raw, ".mp3")]
    [InlineData(null, "audio/mp4", null, null, PreferredFormatEnum.Raw, ".m4a")]
    [InlineData(null, null, null, null, PreferredFormatEnum.Raw, ".flac")]
    public void A_track_is_named_by_the_format_it_streams_in(string? suffix, string? contentType, string? transcodedSuffix, string? transcodedContentType, PreferredFormatEnum preferred, string expected)
    {
        Assert.Equal(expected, SubSonicTrackFormat.Extension(Song(suffix, contentType, transcodedSuffix, transcodedContentType), preferred));
    }

    [Fact]
    public void The_servers_transcoded_suffix_is_read_from_a_search_result()
    {
        SubSonicSearchSong song = JsonSerializer.Deserialize<SubSonicSearchSong>(
            "{\"id\":\"7\",\"title\":\"t\",\"artist\":\"a\",\"suffix\":\"flac\",\"transcodedSuffix\":\"mp3\",\"transcodedContentType\":\"audio/mpeg\"}")!;

        Assert.Equal(".mp3", SubSonicTrackFormat.Extension(song, PreferredFormatEnum.Raw));
    }

    [Theory]
    [InlineData("/d/01.flac", AudioFormat.MP3, ".mp3")]
    [InlineData("/d/01.m4a", AudioFormat.Opus, ".opus")]
    [InlineData("/d/01.mp3", AudioFormat.MP3, null)]
    [InlineData("/d/01.aac", AudioFormat.AAC, null)]
    [InlineData("/d/01.m4a", AudioFormat.ALAC, null)]
    [InlineData("/d/01.flac", AudioFormat.Unknown, null)]
    [InlineData("/d/01.aiff", AudioFormat.WAV, null)]
    [InlineData("/d/01.ogg", AudioFormat.FLAC, null)]
    [InlineData("/d/01.ogg", AudioFormat.Opus, null)]
    [InlineData("/d/01.aiff", AudioFormat.FLAC, ".flac")]
    public void A_file_whose_extension_names_another_codec_is_corrected(string path, AudioFormat actual, string? expected)
    {
        Assert.Equal(expected, SubSonicTrackFormat.Corrected(path, actual));
    }
}

// A SubSonic album with no year was titled with the current year, which Lidarr reads as its release year.
public class SubSonicReleaseYearTests
{
    [Fact]
    public void An_album_with_no_year_has_none_in_its_title()
    {
        AlbumData album = new("SubSonic", "SubSonicDownloadProtocol") { AlbumName = "Album", ArtistName = "Artist", ReleaseDate = string.Empty, ReleaseDatePrecision = "year" };
        album.ParseReleaseDate();

        Assert.DoesNotContain("(", album.ToReleaseInfo().Title);
    }

    [Fact]
    public void An_albums_server_added_date_is_not_its_release_year()
    {
        SubSonicSearchAlbum album = JsonSerializer.Deserialize<SubSonicSearchAlbum>("{\"id\":\"1\",\"name\":\"Album\",\"artist\":\"Artist\",\"created\":\"2019-01-01T00:00:00Z\"}")!;

        Assert.Equal(string.Empty, album.YearString);
    }
}
