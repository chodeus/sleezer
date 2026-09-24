using Newtonsoft.Json.Linq;
using NzbDrone.Plugin.Sleezer.Core.Deezer;
using Xunit;

namespace Sleezer.Tests;

public class DeezerArlCheckTests
{
    private static JObject UserData(long userId, bool webStreaming) => JObject.Parse($$"""
        { "USER": { "USER_ID": {{userId}}, "OPTIONS": { "web_streaming": {{(webStreaming ? "true" : "false")}} } }, "checkForm": "fake-check-form" }
        """);

    [Fact]
    public void Signed_in_account_is_valid() =>
        Assert.True(DeezerArlCheck.HasSignedInUser(UserData(1234567, webStreaming: true)));

    [Fact]
    public void Dead_arl_session_is_invalid_even_without_streaming()
    {
        // Measured shape of a rejected ARL: anonymous user, web_streaming false.
        Assert.False(DeezerArlCheck.HasSignedInUser(UserData(0, webStreaming: false)));
    }

    [Fact]
    public void Anonymous_session_with_streaming_is_invalid() =>
        Assert.False(DeezerArlCheck.HasSignedInUser(UserData(0, webStreaming: true)));

    [Fact]
    public void Missing_user_data_is_invalid()
    {
        Assert.False(DeezerArlCheck.HasSignedInUser(null));
        Assert.False(DeezerArlCheck.HasSignedInUser(JObject.Parse("{}")));
    }
}
