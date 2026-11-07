using NzbDrone.Plugin.Sleezer.Core.Utilities;
using NzbDrone.Plugin.Sleezer.Metadata.FFmpeg;
using Xunit;

namespace Sleezer.Tests;

public class ConversionRuleGuardTests
{
    private static string? Reason(AudioFormat source, int? bitrate, int? bitDepth, AudioFormat target, int? targetBitrate = null, int? targetBitDepth = null) =>
        ConversionGuard.BlockReason(source, bitrate, bitDepth, target, targetBitrate, targetBitDepth);

    [Theory]
    [InlineData(AudioFormat.MP3, 320, AudioFormat.FLAC, null)]
    [InlineData(AudioFormat.MP3, 128, AudioFormat.MP3, 320)]
    [InlineData(AudioFormat.MP3, null, AudioFormat.MP3, 128)]
    [InlineData(AudioFormat.Opus, 0, AudioFormat.MP3, 128)]
    public void A_lossy_conversion_that_could_fake_quality_is_blocked(AudioFormat source, int? bitrate, AudioFormat target, int? targetBitrate)
    {
        Assert.NotNull(Reason(source, bitrate, null, target, targetBitrate));
    }

    [Theory]
    [InlineData(AudioFormat.FLAC, 16, 24)]
    [InlineData(AudioFormat.FLAC, null, 16)]
    [InlineData(AudioFormat.FLAC, 0, 16)]
    public void A_lossless_conversion_that_could_fake_bit_depth_is_blocked(AudioFormat source, int? bitDepth, int targetBitDepth)
    {
        Assert.NotNull(Reason(source, null, bitDepth, AudioFormat.FLAC, targetBitDepth: targetBitDepth));
    }

    [Theory]
    [InlineData(AudioFormat.MP3, 320, null, AudioFormat.MP3, 128, null)]
    [InlineData(AudioFormat.MP3, 128, null, AudioFormat.Opus, null, null)]
    [InlineData(AudioFormat.FLAC, null, 24, AudioFormat.FLAC, null, 16)]
    [InlineData(AudioFormat.FLAC, null, null, AudioFormat.MP3, 320, null)]
    [InlineData(AudioFormat.FLAC, null, null, AudioFormat.FLAC, null, null)]
    public void A_conversion_that_keeps_or_lowers_quality_goes_ahead(AudioFormat source, int? bitrate, int? bitDepth, AudioFormat target, int? targetBitrate, int? targetBitDepth)
    {
        Assert.Null(Reason(source, bitrate, bitDepth, target, targetBitrate, targetBitDepth));
    }

    [Theory]
    [InlineData("s16", 16)]
    [InlineData("s24p", 24)]
    [InlineData("fltp", 32)]
    [InlineData("flt", 32)]
    [InlineData("dbl", 64)]
    [InlineData("u8", null)]
    public void A_sample_format_reads_as_its_bit_depth(string sampleFormat, int? bits)
    {
        Assert.Equal(bits, ConversionGuard.BitDepthOf(sampleFormat));
    }
}
