using NzbDrone.Core.Music;
using NzbDrone.Plugin.Sleezer.Core.PostProcessing;
using Xunit;

namespace Sleezer.Tests;

// Live 2026-08-26: no Digital Media release existed yet, so the fallback wrote an 8-track
// CD-R's identity onto a 7-track Qobuz single; Lidarr scored the right release at 39.5%.
public class PreImportTaggerFallbackGuardTests
{
    private static AlbumRelease R(int trackCount, params string[] formats) => new()
    {
        ForeignReleaseId = "release",
        Title = "release",
        TrackCount = trackCount,
        Media = [.. formats.Select((f, i) => new Medium { Number = i + 1, Format = f })]
    };

    [Fact]
    public void Rejects_a_physical_release_for_a_storefront_download()
    {
        Assert.False(TitleFallbackGuard.IsSafeTarget(R(7, "CD-R"), 7, 7, preferDigitalMedia: true));
    }

    [Fact]
    public void Accepts_a_digital_release_for_a_storefront_download()
    {
        Assert.True(TitleFallbackGuard.IsSafeTarget(R(7, "Digital Media"), 7, 7, preferDigitalMedia: true));
    }

    // Scoped to digital sources — slskd and friends still tag off physical releases.
    [Fact]
    public void Accepts_a_physical_release_when_the_source_is_not_a_storefront()
    {
        Assert.True(TitleFallbackGuard.IsSafeTarget(R(7, "CD-R"), 7, 7, preferDigitalMedia: false));
    }

    [Theory]
    [InlineData(8, 7)]
    [InlineData(7, 8)]
    public void Rejects_a_release_of_a_different_length(int releaseTrackCount, int localTrackCount)
    {
        Assert.False(TitleFallbackGuard.IsSafeTarget(
            R(releaseTrackCount, "Digital Media"), releaseTrackCount, localTrackCount, preferDigitalMedia: true));
    }

    [Fact]
    public void Rejects_a_release_of_a_different_length_for_any_source()
    {
        Assert.False(TitleFallbackGuard.IsSafeTarget(R(8, "CD"), 8, 7, preferDigitalMedia: false));
    }

    [Fact]
    public void Rejects_a_missing_release()
    {
        Assert.False(TitleFallbackGuard.IsSafeTarget(null, 7, 7, preferDigitalMedia: false));
    }

    // No media at all is not digital — an unknown format must not read as "not physical".
    [Fact]
    public void Rejects_a_release_with_no_media_for_a_storefront_download()
    {
        Assert.False(TitleFallbackGuard.IsSafeTarget(R(7), 7, 7, preferDigitalMedia: true));
    }
}
