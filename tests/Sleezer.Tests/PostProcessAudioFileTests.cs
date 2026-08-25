using NzbDrone.Plugin.Sleezer.Core.Utilities;
using Xunit;

namespace Sleezer.Tests;

// The corruption scan, the pre-import tagger and the slskd manager each carried a
// private copy of this set (issue #90). These lock what the surviving copy accepts,
// and that it stays distinct from GetAudioCodecFromExtension's codec map — folding
// the two together looks like a tidy-up and silently changes what gets opened.
public class PostProcessAudioFileTests
{
    [Theory]
    [InlineData("track.flac")]
    [InlineData("track.mp3")]
    [InlineData("track.m4a")]
    [InlineData("track.ogg")]
    [InlineData("track.opus")]
    [InlineData("track.wav")]
    [InlineData("track.wma")]
    [InlineData("track.aac")]
    [InlineData("track.aiff")]
    [InlineData("track.aif")]
    [InlineData("track.ape")]
    [InlineData("track.wv")]
    [InlineData("track.alac")]
    [InlineData("track.m4b")]
    [InlineData("track.m4p")]
    [InlineData("track.mp2")]
    [InlineData("track.mpc")]
    [InlineData("track.dsf")]
    [InlineData("track.dff")]
    public void Every_extension_the_private_copies_carried_is_still_accepted(string path)
    {
        Assert.True(AudioFormatHelper.IsPostProcessAudioFile(path));
    }

    // WavPack, Musepack, DSD and audiobook m4b have no codec-map entry, so reusing
    // GetAudioCodecFromExtension here would stop scanning them.
    [Theory]
    [InlineData("track.wv")]
    [InlineData("track.mpc")]
    [InlineData("track.dsf")]
    [InlineData("track.dff")]
    [InlineData("track.m4b")]
    [InlineData("track.m4p")]
    [InlineData("track.mp2")]
    public void Accepts_lossless_containers_the_codec_map_does_not_name(string path)
    {
        Assert.True(AudioFormatHelper.IsPostProcessAudioFile(path));
        Assert.False(AudioFormatHelper.IsAudioFilename(path));
    }

    // The reverse: the codec map names formats that are not library audio. Scanning a
    // .mid would hand TagLib a file with no audio properties to read.
    [Theory]
    [InlineData("cue.mid")]
    [InlineData("cue.midi")]
    [InlineData("voice.amr")]
    [InlineData("surround.ac3")]
    [InlineData("surround.eac3")]
    public void Rejects_codec_map_entries_that_are_not_library_audio(string path)
    {
        Assert.False(AudioFormatHelper.IsPostProcessAudioFile(path));
        Assert.True(AudioFormatHelper.IsAudioFilename(path));
    }

    [Theory]
    [InlineData("cover.jpg")]
    [InlineData("rip.log")]
    [InlineData("rip.cue")]
    [InlineData("album.nfo")]
    [InlineData("noextension")]
    [InlineData("")]
    [InlineData(null)]
    public void Rejects_non_audio_artifacts(string? path)
    {
        Assert.False(AudioFormatHelper.IsPostProcessAudioFile(path));
    }

    // PreImportTagger's copy compared with OrdinalIgnoreCase; peers send ".FLAC".
    [Theory]
    [InlineData("track.FLAC")]
    [InlineData("track.Mp3")]
    [InlineData(@"peer\dir\TRACK.WV")]
    public void Matches_regardless_of_extension_casing(string path)
    {
        Assert.True(AudioFormatHelper.IsPostProcessAudioFile(path));
    }
}
