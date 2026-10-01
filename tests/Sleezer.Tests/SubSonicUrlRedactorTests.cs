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
    public void Login_parameters_are_removed_and_the_rest_kept(string url, string expected) =>
        Assert.Equal(expected, SubSonicUrlRedactor.Redact(url));

    [Fact]
    public void Parameters_that_only_start_with_a_login_letter_are_kept() =>
        Assert.Equal("http://x/rest/a?songCount=5&type=album&size=1", SubSonicUrlRedactor.Redact("http://x/rest/a?songCount=5&type=album&size=1"));

    [Fact]
    public void A_missing_url_is_empty() => Assert.Equal(string.Empty, SubSonicUrlRedactor.Redact(null));
}
