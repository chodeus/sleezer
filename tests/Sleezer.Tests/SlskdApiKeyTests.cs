using System.Net;
using FluentValidation.Results;
using NzbDrone.Common.Http;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek;
using Xunit;

namespace Sleezer.Tests;

public class SlskdApiKeyTests
{
    [Fact]
    public void a_set_key_is_sent()
    {
        HttpRequest request = new HttpRequestBuilder("http://localhost:5030/api/v0/searches").WithApiKey("secret").Build();

        Assert.Equal("secret", request.Headers.GetSingleValue("X-API-KEY"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void no_key_sends_no_header(string? key)
    {
        HttpRequest request = new HttpRequestBuilder("http://localhost:5030/api/v0/searches").WithApiKey(key).Build();

        Assert.Null(request.Headers.GetSingleValue("X-API-KEY"));
    }

    [Fact]
    public void the_settings_accept_an_empty_key()
    {
        ValidationResult result = new SlskdSettingsValidator().Validate(new SlskdSettings { BaseUrl = "http://localhost:5030", ApiKey = "" });

        Assert.DoesNotContain(result.Errors, e => e.PropertyName == nameof(SlskdSettings.ApiKey));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "", "The request was refused (401 Unauthorized). If slskd's authentication is on, set its API key; otherwise check any proxy in front of slskd.")]
    [InlineData(HttpStatusCode.Forbidden, "", "The request was refused (403 Forbidden). If slskd's authentication is on, set its API key; otherwise check any proxy in front of slskd.")]
    [InlineData(HttpStatusCode.Forbidden, "stale", "The request was refused (403 Forbidden). Check the API key, and any proxy in front of slskd.")]
    public void a_refused_request_points_at_the_api_key(HttpStatusCode status, string key, string message)
    {
        ValidationFailure? failure = SlskdApiKey.Refused(status, key);

        Assert.Equal(nameof(SlskdSettings.ApiKey), failure?.PropertyName);
        Assert.Equal(message, failure?.ErrorMessage);
    }

    [Fact]
    public void another_failure_is_not_blamed_on_the_key() =>
        Assert.Null(SlskdApiKey.Refused(HttpStatusCode.BadGateway, ""));
}
