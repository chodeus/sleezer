using System.Net;
using System.Text;
using System.Web;
using TidalSharp;
using TidalSharp.Data;
using Xunit;

namespace Sleezer.Tests;

public class TidalTokenRefreshTests
{
    private const string Refreshed = """{"access_token":"new-access","token_type":"Bearer","expires_in":14400}""";

    private static TidalUser Login(bool signedInWithPkce = false) =>
        new(new OAuthTokenData { AccessToken = "old-access", RefreshToken = "refresh" }, null, signedInWithPkce, DateTime.UtcNow);

    private static System.Collections.Specialized.NameValueCollection SentForm(FakeHttpClient endpoint) =>
        HttpUtility.ParseQueryString(Encoding.UTF8.GetString(endpoint.Requests.Single().ContentData));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_login_refreshes_under_the_android_client(bool signedInWithPkce)
    {
        // A device login refreshed under any other client loses playback (#173).
        var endpoint = new FakeHttpClient(r => FakeHttpClient.Respond(r, HttpStatusCode.OK, Refreshed));
        var user = Login(signedInWithPkce);

        Assert.True(await new Session(endpoint).AttemptTokenRefresh(user));

        var form = SentForm(endpoint);
        Assert.Equal("refresh", form["refresh_token"]);
        Assert.Equal(Globals.CLIENT_ID_PKCE, form["client_id"]);
        Assert.Equal(Globals.CLIENT_SECRET_PKCE, form["client_secret"]);
        Assert.Equal("new-access", user.AccessToken);
    }

    [Fact]
    public async Task A_configured_playback_client_is_used_for_the_refresh()
    {
        var endpoint = new FakeHttpClient(r => FakeHttpClient.Respond(r, HttpStatusCode.OK, Refreshed));

        Assert.True(await new Session(endpoint, refreshClient: new ClientCredentials("custom-id", "custom-secret")).AttemptTokenRefresh(Login()));

        var form = SentForm(endpoint);
        Assert.Equal("custom-id", form["client_id"]);
        Assert.Equal("custom-secret", form["client_secret"]);
    }

    [Fact]
    public async Task A_saved_login_is_refreshed_on_load_before_it_expires()
    {
        // A login saved under a refused client must move over on the first start, not when it expires.
        TidalUser? persisted = null;
        var http = new FakeHttpClient(r => r.Url.ToString().Contains("oauth2/token", StringComparison.Ordinal)
            ? FakeHttpClient.Respond(r, HttpStatusCode.OK, Refreshed)
            : FakeHttpClient.Respond(r, HttpStatusCode.OK, """{"sessionId":"session","userId":1,"countryCode":"CA"}"""));
        var client = new TidalClient(null, http);

        Assert.True(await client.LoadFromTokens("old-access", "refresh", "Bearer", 1, DateTime.UtcNow.AddHours(1), onTokensRefreshed: user => persisted = user));

        Assert.Equal("new-access", persisted?.AccessToken);
        Assert.Equal("new-access", client.ActiveUser!.AccessToken);
    }

    [Fact]
    public async Task A_refresh_that_cannot_reach_tidal_keeps_the_saved_login()
    {
        var http = new FakeHttpClient(r => r.Url.ToString().Contains("oauth2/token", StringComparison.Ordinal)
            ? throw new WebException("connection reset")
            : FakeHttpClient.Respond(r, HttpStatusCode.OK, """{"sessionId":"session","userId":1,"countryCode":"CA"}"""));
        var client = new TidalClient(null, http);

        Assert.False(await client.LoadFromTokens("old-access", "refresh", "Bearer", 1, DateTime.UtcNow.AddHours(1)));

        Assert.Equal("old-access", client.ActiveUser!.AccessToken);
        Assert.Equal("session", client.ActiveUser.SessionID);
    }

    [Fact]
    public void A_rejection_is_logged_by_its_error_code_only()
    {
        string logged = Session.RefreshError("""{"status":400,"error":"invalid_grant","sub_status":11101,"error_description":"refresh_token=SECRET-REFRESH rejected"}""");

        Assert.Equal("invalid_grant 11101", logged);
        Assert.DoesNotContain("SECRET", Session.RefreshError("<html>refresh_token=SECRET-REFRESH</html>"));
        Assert.Equal("unrecognised error", Session.RefreshError("""{"error":"refresh_token=SECRET-REFRESH","sub_status":"SECRET 1"}"""));
    }

    [Fact]
    public async Task A_rejected_refresh_keeps_the_saved_token()
    {
        var endpoint = new FakeHttpClient(r => FakeHttpClient.Respond(r, HttpStatusCode.BadRequest, """{"status":400,"error":"invalid_grant","sub_status":11101}"""));
        var user = Login();

        Assert.False(await new Session(endpoint).AttemptTokenRefresh(user));
        Assert.Equal("old-access", user.AccessToken);
    }
}
