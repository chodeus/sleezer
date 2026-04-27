using TidalSharp;
using TidalSharp.Data;
using Xunit;

namespace Sleezer.Tests;

public class LosslessGuardTests
{
    [Theory]
    [InlineData(AudioQuality.LOSSLESS, true)]
    [InlineData(AudioQuality.HI_RES_LOSSLESS, true)]
    [InlineData(AudioQuality.HIGH, false)]
    [InlineData(AudioQuality.LOW, false)]
    public void IsLosslessTier_classifies_each_quality(AudioQuality q, bool expected)
    {
        Assert.Equal(expected, LosslessGuard.IsLosslessTier(q));
    }

    [Theory]
    [InlineData("FLAC", true)]
    [InlineData("flac", true)]
    [InlineData("Flac", true)]
    [InlineData("MP4A", false)]
    [InlineData("mp4a.40.2", false)]
    [InlineData("AAC", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void CodecIsLossless_recognises_flac_only(string? codec, bool expected)
    {
        Assert.Equal(expected, LosslessGuard.CodecIsLossless(codec));
    }

    [Fact]
    public void ShouldRejectAsSilentDowngrade_fires_when_lossless_request_gets_aac()
    {
        Assert.True(LosslessGuard.ShouldRejectAsSilentDowngrade(AudioQuality.LOSSLESS, "MP4A"));
        Assert.True(LosslessGuard.ShouldRejectAsSilentDowngrade(AudioQuality.HI_RES_LOSSLESS, "mp4a.40.2"));
    }

    [Fact]
    public void ShouldRejectAsSilentDowngrade_does_not_fire_when_lossless_request_gets_flac()
    {
        Assert.False(LosslessGuard.ShouldRejectAsSilentDowngrade(AudioQuality.LOSSLESS, "FLAC"));
        Assert.False(LosslessGuard.ShouldRejectAsSilentDowngrade(AudioQuality.HI_RES_LOSSLESS, "flac"));
    }

    [Fact]
    public void ShouldRejectAsSilentDowngrade_does_not_fire_for_lossy_tier_requests()
    {
        // Asking HIGH and getting AAC is the EXPECTED behaviour, not a downgrade.
        Assert.False(LosslessGuard.ShouldRejectAsSilentDowngrade(AudioQuality.HIGH, "MP4A"));
        Assert.False(LosslessGuard.ShouldRejectAsSilentDowngrade(AudioQuality.LOW, "MP4A"));
    }

    [Fact]
    public void ShouldRejectAsSilentDowngrade_does_not_fire_when_codec_unknown()
    {
        // null codec means we couldn't parse the manifest — bail out rather than
        // trigger a false-positive download failure.
        Assert.False(LosslessGuard.ShouldRejectAsSilentDowngrade(AudioQuality.LOSSLESS, null));
        Assert.False(LosslessGuard.ShouldRejectAsSilentDowngrade(AudioQuality.LOSSLESS, ""));
    }
}
