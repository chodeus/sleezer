using NLog;
using NzbDrone.Core.Parser;
using NzbDrone.Plugin.Sleezer.Core.Model;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek;
using Xunit;

namespace Sleezer.Tests;

// Same "[FLAC 24bit 96kHz]" form the Qobuz titles use; Lidarr reads only the bit depth from it.
public class SlskdSampleRateTitleTests
{
    private static string TitleOf(AudioFormat codec, int bitDepth, int sampleRate, int bitrate = 0) =>
        new AlbumData("Slskd", "SoulseekDownloadProtocol")
        {
            ArtistName = "Muse",
            AlbumName = "The Resistance",
            Codec = codec,
            BitDepth = bitDepth,
            SampleRate = sampleRate,
            Bitrate = bitrate,
            SourceTag = "WEB"
        }.ToReleaseInfo().Title;

    [Theory]
    [InlineData(24, 96000, "[FLAC 24bit 96kHz]")]
    [InlineData(16, 44100, "[FLAC 16bit 44.1kHz]")]
    [InlineData(24, 0, "[FLAC 24bit]")]
    public void lossless_titles_carry_the_sample_rate_when_known(int bitDepth, int sampleRate, string expected)
    {
        Assert.Contains(expected, TitleOf(AudioFormat.FLAC, bitDepth, sampleRate));
    }

    [Theory]
    [InlineData(24, 96000, "FLAC 24bit")]
    [InlineData(16, 44100, "FLAC")]
    public void lidarr_still_reads_the_quality_from_the_title(int bitDepth, int sampleRate, string expected)
    {
        Assert.Equal(expected, QualityParser.ParseQuality(TitleOf(AudioFormat.FLAC, bitDepth, sampleRate), null, 0).Quality.Name);
    }

    [Fact]
    public void lossy_titles_are_unchanged()
    {
        Assert.Equal("Muse - The Resistance [MP3 320kbps] [WEB]", TitleOf(AudioFormat.MP3, 0, 44100, bitrate: 320));
    }

    [Fact]
    public void a_folder_with_one_sample_rate_puts_it_in_the_title()
    {
        SlskdItemsParser parser = new(LogManager.GetLogger("tests"));
        IGrouping<string, SlskdFileData> group = new[]
            {
                @"Music\Muse\The Resistance\01 Uprising.flac",
                @"Music\Muse\The Resistance\02 Resistance.flac",
            }
            .Select(f => new SlskdFileData(f, null, 24, 60_000_000, 300, ".flac", 96000, 0, false))
            .GroupBy(f => SlskdTextProcessor.GetMergedDirectoryKey(f.Filename))
            .Single();

        AlbumData album = parser.CreateAlbumData(
            group,
            new SlskdSearchData("Muse", "The Resistance", Interactive: true, ExpandDirectory: false,
                MinimumFiles: 1, MaximumFiles: 4, TrackCount: 2, Tracks: [], TargetVariantTypes: null, AlbumType: "Album"),
            new SlskdFolderData(@"Music\Muse\The Resistance", "Muse", "The Resistance", "", "peer", true, 1_000_000, 0, [], 0, 0, 0, [], 0),
            new SlskdSettings(),
            expectedTrackCount: 2);

        Assert.Equal(96000, album.SampleRate);
        Assert.Contains("[FLAC 24bit 96kHz]", album.ToReleaseInfo().Title);
    }
}
