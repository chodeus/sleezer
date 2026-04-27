using TidalSharp;
using Xunit;

namespace Sleezer.Tests;

public class ExpiredTokenDetectorTests
{
    [Fact]
    public void Returns_true_for_explicit_token_expired_message()
    {
        const string body = """{"status":401,"subStatus":11003,"userMessage":"The token has expired. (Expired on time)"}""";
        Assert.True(ExpiredTokenDetector.LooksExpired(body, requestHadCountryCode: true));
    }

    [Fact]
    public void Returns_true_for_missing_countrycode_when_request_actually_had_one()
    {
        // Tidal issue #42 sentinel — the body lies, the real cause is an expired session.
        const string body = """{"status":401,"userMessage":"countryCode parameter missing"}""";
        Assert.True(ExpiredTokenDetector.LooksExpired(body, requestHadCountryCode: true));
    }

    [Fact]
    public void Returns_false_for_missing_countrycode_when_request_was_actually_missing_one()
    {
        // Don't paper over a real client bug with a refresh.
        const string body = """{"status":401,"userMessage":"countryCode parameter missing"}""";
        Assert.False(ExpiredTokenDetector.LooksExpired(body, requestHadCountryCode: false));
    }

    [Fact]
    public void Returns_false_for_unrelated_401_message()
    {
        const string body = """{"status":401,"userMessage":"Authorization required"}""";
        Assert.False(ExpiredTokenDetector.LooksExpired(body, requestHadCountryCode: true));
    }

    [Fact]
    public void Returns_false_for_non_json_body()
    {
        Assert.False(ExpiredTokenDetector.LooksExpired("Service Unavailable", requestHadCountryCode: true));
    }

    [Fact]
    public void Returns_false_for_empty_or_null_body()
    {
        Assert.False(ExpiredTokenDetector.LooksExpired(null, requestHadCountryCode: true));
        Assert.False(ExpiredTokenDetector.LooksExpired(string.Empty, requestHadCountryCode: true));
    }

    [Fact]
    public void Returns_false_for_json_without_userMessage()
    {
        const string body = """{"status":401}""";
        Assert.False(ExpiredTokenDetector.LooksExpired(body, requestHadCountryCode: true));
    }
}
