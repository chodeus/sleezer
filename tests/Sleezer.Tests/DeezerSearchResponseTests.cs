using Newtonsoft.Json;
using NzbDrone.Core.Download.Clients.Deezer;
using NzbDrone.Plugin.Sleezer.Core.Deezer;
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

    // A rejected request usually carries BOTH an error and no results.data, and the ARL
    // guard would otherwise blame the credentials for whatever Deezer actually reported.
    [Fact]
    public void A_reported_error_is_read_before_the_arl_guard()
    {
        var wrapper = Parse("""{"error":{"VALID_TOKEN_REQUIRED":"Invalid CSRF token"},"results":{}}""");

        var ex = Assert.Throws<InvalidOperationException>(() => DeezerSearchResponseReader.Read(wrapper, "<body>"));

        Assert.Contains("VALID_TOKEN_REQUIRED", ex.Message);
        Assert.DoesNotContain("ARL", ex.Message);
        Assert.Equal("<body>", ex.Data["DeezerResponseSnippet"]);
    }

    // Missing results.data with nothing else said is the shape a dead ARL produces.
    [Fact]
    public void Missing_results_data_alone_still_blames_the_arl()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => DeezerSearchResponseReader.Read(Parse("""{"error":[],"results":{}}"""), "<body>"));

        Assert.Contains("ARL is missing or invalid", ex.Message);
    }

    [Fact]
    public void An_empty_data_array_is_an_ordinary_empty_result()
    {
        var wrapper = Parse("""{"error":[],"results":{"data":[],"total":0}}""");

        Assert.Equal(DeezerSearchResponseReader.Outcome.Empty, DeezerSearchResponseReader.Read(wrapper, "<body>"));
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
