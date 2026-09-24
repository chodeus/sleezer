using NzbDrone.Plugin.Sleezer.Core.Utilities;
using Xunit;

namespace Sleezer.Tests;

public class StoreReleaseGuidTests
{
    [Theory]
    [InlineData("1_Qobuz-abc123xyz-FLACHiRes24Bit96kHz", "1_Qobuz-abc123xyz")]
    [InlineData("2_Deezer-123456789-9", "2_Deezer-123456789")]
    [InlineData("3_Tidal-987654321-HI_RES_LOSSLESS", "3_Tidal-987654321")]
    [InlineData("Qobuz-abc123xyz-FLACLossless", "Qobuz-abc123xyz")]
    public void AlbumKey_drops_the_quality_tier(string guid, string album) =>
        Assert.Equal(album, StoreReleaseGuid.AlbumKey(guid));

    [Theory]
    [InlineData("4_Slskd-peer-1a2b3c")]
    [InlineData("5_bandcamp-https://artist.bandcamp.com/album/x-flac")]
    [InlineData("1_Qobuz-abc123xyz")]
    [InlineData("1_Qobuz-abc-def-FLACLossless")]
    [InlineData("")]
    [InlineData(null)]
    public void AlbumKey_is_null_for_anything_but_a_store_release(string? guid) =>
        Assert.Null(StoreReleaseGuid.AlbumKey(guid));

    [Fact]
    public void AlbumKey_reads_the_guid_the_parsers_build() =>
        Assert.Equal("Deezer-123456789", StoreReleaseGuid.AlbumKey(StoreReleaseGuid.Create(StoreReleaseGuid.Store.Deezer, 123456789L, 9)));
}
