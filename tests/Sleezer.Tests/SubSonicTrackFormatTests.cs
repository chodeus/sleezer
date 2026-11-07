using System.Text.Json;
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
    [InlineData("flac", null, null, null, PreferredFormatEnum.Aac, ".aac")]
    [InlineData("MP3", null, null, null, PreferredFormatEnum.Raw, ".mp3")]
    [InlineData(null, "audio/flac", null, null, PreferredFormatEnum.Raw, ".flac")]
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
    public void A_file_whose_extension_names_another_codec_is_corrected(string path, AudioFormat actual, string? expected)
    {
        Assert.Equal(expected, SubSonicTrackFormat.Corrected(path, actual));
    }
}
