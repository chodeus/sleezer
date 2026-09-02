using Newtonsoft.Json.Linq;
using NzbDrone.Plugin.Sleezer.Core.Deezer;
using Xunit;

namespace Sleezer.Tests;

// Deezer computes RIGHTS server-side for the session's country, so a track the account cannot
// stream reads false here. Live 2026-09-02: The Streets "Dry Your Eyes" carries a live B-side
// Deezer sells only in Germany, which failed mid-download instead of at search time.
public class DeezerTrackRightsTests
{
    [Fact]
    public void A_subscription_streamable_track_is_available()
    {
        var entry = JToken.Parse("""{"RIGHTS":{"STREAM_SUB_AVAILABLE":true}}""");

        Assert.True(DeezerTrackRights.Streamable(entry));
    }

    [Fact]
    public void An_ad_supported_track_is_available()
    {
        var entry = JToken.Parse("""{"RIGHTS":{"STREAM_ADS_AVAILABLE":true,"STREAM_SUB_AVAILABLE":false}}""");

        Assert.True(DeezerTrackRights.Streamable(entry));
    }

    [Fact]
    public void A_track_blocked_in_this_country_is_unavailable()
    {
        var entry = JToken.Parse("""{"RIGHTS":{"STREAM_ADS_AVAILABLE":false,"STREAM_SUB_AVAILABLE":false}}""");

        Assert.False(DeezerTrackRights.Streamable(entry));
    }

    // Fail open: without a RIGHTS block we know nothing, and guessing "blocked" would hide
    // every album on any payload shape that omits it.
    [Theory]
    [InlineData("""{"SNG_ID":"1"}""")]
    [InlineData("""{"RIGHTS":null}""")]
    public void A_missing_rights_block_is_unknown(string json)
    {
        Assert.Null(DeezerTrackRights.Streamable(JToken.Parse(json)));
    }

    [Fact]
    public void A_missing_entry_is_unknown()
    {
        Assert.Null(DeezerTrackRights.Streamable(null));
    }
}
