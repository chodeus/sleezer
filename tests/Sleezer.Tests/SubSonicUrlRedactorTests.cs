using NzbDrone.Plugin.Sleezer.Indexers.SubSonic;
using Xunit;

namespace Sleezer.Tests;

public class SubSonicUrlRedactorTests
{
    [Theory]
    [InlineData(
        "http://music.example/rest/search3.view?query=abc&u=user-a&v=1.16.1&c=Sleezer&t=0123456789abcdef0123456789abcdef&s=salt123&f=json",
        "http://music.example/rest/search3.view?query=abc&u=(removed)&v=1.16.1&c=Sleezer&t=(removed)&s=(removed)&f=json")]
    [InlineData(
        "http://music.example/rest/ping.view?u=user-a&p=secret-a&f=json",
        "http://music.example/rest/ping.view?u=(removed)&p=(removed)&f=json")]
    [InlineData(
        "http://music.example/rest/getAlbum.view?id=al-1&songCount=5&U=user-a",
        "http://music.example/rest/getAlbum.view?id=al-1&songCount=5&U=(removed)")]
    public void Login_values_are_replaced_and_the_rest_kept(string url, string expected) =>
        Assert.Equal(expected, SubSonicUrlRedactor.Redact(url));

    [Fact]
    public void Parameters_that_only_start_with_a_login_letter_are_kept() =>
        Assert.Equal("http://x/rest/a?songCount=5&type=album&size=1", SubSonicUrlRedactor.Redact("http://x/rest/a?songCount=5&type=album&size=1"));

    [Fact]
    public void A_missing_url_is_empty() => Assert.Equal(string.Empty, SubSonicUrlRedactor.Redact(null));
}

public class SubSonicLoginInterceptorTests
{
    private static NzbDrone.Common.Http.HttpRequest Request(string url, string? login)
    {
        NzbDrone.Common.Http.HttpRequest request = new(url);
        request.Headers["User-Agent"] = "agent-a";
        if (login != null)
            request.Headers[SubSonicLoginInterceptor.Header] = login;
        return request;
    }

    [Theory]
    [InlineData("http://music.example/rest/search3.view?query=abc&f=json", "http://music.example/rest/search3.view?query=abc&f=json&u=user-a&t=tok&s=salt")]
    [InlineData("http://music.example/rest/ping.view", "http://music.example/rest/ping.view?u=user-a&t=tok&s=salt")]
    public void The_login_moves_from_the_header_into_the_url(string url, string expected)
    {
        var sent = new SubSonicLoginInterceptor().PreRequest(Request(url, "u=user-a&t=tok&s=salt"));

        Assert.Equal(expected, sent.Url.FullUri);
        Assert.Null(sent.Headers.GetSingleValue(SubSonicLoginInterceptor.Header));
        Assert.Equal("agent-a", sent.Headers.GetSingleValue("User-Agent"));
    }

    [Fact]
    public void Other_requests_pass_through_unchanged()
    {
        var sent = new SubSonicLoginInterceptor().PreRequest(Request("http://other.example/api?x=1", null));

        Assert.Equal("http://other.example/api?x=1", sent.Url.FullUri);
    }
}
