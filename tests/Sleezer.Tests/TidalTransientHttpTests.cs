using System.Net;
using NzbDrone.Common.Http;
using TidalSharp;
using Xunit;

namespace Sleezer.Tests;

public class TidalTransientHttpTests
{
    private static Task<HttpResponse> Send(FakeHttpClient http) =>
        TransientHttp.SendAsync(http, new HttpRequestBuilder("https://api.tidal.invalid/v1/tracks/1"), CancellationToken.None);

    [Fact]
    public async Task A_rate_limit_is_retried_after_its_Retry_After()
    {
        int calls = 0;
        var http = new FakeHttpClient(r => ++calls == 1 ? FakeHttpClient.Respond(r, HttpStatusCode.TooManyRequests, retryAfter: "0") : FakeHttpClient.Respond(r, HttpStatusCode.OK));

        var response = await Send(http);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, http.Requests.Count);
    }

    [Fact]
    public async Task A_server_error_is_retried_a_bounded_number_of_times()
    {
        var http = new FakeHttpClient(r => FakeHttpClient.Respond(r, HttpStatusCode.ServiceUnavailable, retryAfter: "0"));

        var response = await Send(http);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(TransientHttp.MaxAttempts, http.Requests.Count);
    }

    [Fact]
    public async Task A_client_error_is_not_retried()
    {
        var http = new FakeHttpClient(r => FakeHttpClient.Respond(r, HttpStatusCode.Forbidden));

        var response = await Send(http);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task A_dropped_connection_is_retried()
    {
        int calls = 0;
        var http = new FakeHttpClient(r => ++calls == 1 ? throw new WebException("connection reset") : FakeHttpClient.Respond(r, HttpStatusCode.OK));

        var response = await Send(http);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, http.Requests.Count);
    }

    [Theory]
    [InlineData("5", 5)]
    [InlineData("600", 60)]
    public void Retry_After_is_honoured_up_to_a_minute(string retryAfter, int seconds)
    {
        var request = new HttpRequestBuilder("https://api.tidal.invalid/v1/tracks/1").Build();

        Assert.Equal(TimeSpan.FromSeconds(seconds), TransientHttp.Delay(1, FakeHttpClient.Respond(request, HttpStatusCode.TooManyRequests, retryAfter: retryAfter)));
    }

    [Fact]
    public void Without_Retry_After_the_delay_doubles()
    {
        var response = FakeHttpClient.Respond(new HttpRequestBuilder("https://api.tidal.invalid/v1/tracks/1").Build(), HttpStatusCode.BadGateway);

        Assert.Equal([1d, 2d, 4d], new[] { 1, 2, 3 }.Select(attempt => TransientHttp.Delay(attempt, response).TotalSeconds));
    }
}
