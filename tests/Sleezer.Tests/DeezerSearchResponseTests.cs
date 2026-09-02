using Newtonsoft.Json;
using NzbDrone.Core.Download.Clients.Deezer;
using Xunit;

namespace Sleezer.Tests;

// Deezer answers a rejected search with HTTP 200 and a populated top-level error, which is
// otherwise indistinguishable from "this query genuinely matched nothing".
public class DeezerSearchResponseTests
{
    private static DeezerSearchResponseWrapper Parse(string json) =>
        JsonConvert.DeserializeObject<DeezerSearchResponseWrapper>(json)!;

    [Fact]
    public void A_populated_error_object_is_readable()
    {
        var wrapper = Parse("""{"error":{"GATEWAY_ERROR":"invalid api token"},"results":{"data":[],"total":0}}""");

        Assert.True(wrapper.Error?.HasValues);
        Assert.Empty(wrapper.Results.Data);
    }

    // Deezer sends an empty ARRAY, not null, when nothing went wrong.
    [Fact]
    public void An_empty_error_array_does_not_read_as_an_error()
    {
        var wrapper = Parse("""{"error":[],"results":{"data":[],"total":0}}""");

        Assert.False(wrapper.Error?.HasValues);
    }

    [Fact]
    public void A_missing_error_key_does_not_read_as_an_error()
    {
        var wrapper = Parse("""{"results":{"data":[],"total":0}}""");

        Assert.Null(wrapper.Error);
    }

    // The other half of the anomaly test: no results but a non-zero total.
    [Fact]
    public void Total_is_readable_alongside_an_empty_data_array()
    {
        var wrapper = Parse("""{"error":[],"results":{"data":[],"total":34}}""");

        Assert.Equal(34, wrapper.Results.Total);
        Assert.Empty(wrapper.Results.Data);
    }
}
