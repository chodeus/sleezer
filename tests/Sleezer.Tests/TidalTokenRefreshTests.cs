using System.Net;
using System.Text;
using System.Web;
using NzbDrone.Common.Http;
using TidalSharp;
using TidalSharp.Data;
using Xunit;

namespace Sleezer.Tests;

public class TidalTokenRefreshTests
{
    private sealed class TokenEndpoint(
        HttpStatusCode status = HttpStatusCode.OK,
        string body = """{"access_token":"new-access","token_type":"Bearer","expires_in":14400}""") : IHttpClient
    {
        public HttpRequest? Sent { get; private set; }

        public Task<HttpResponse> ExecuteAsync(HttpRequest request)
        {
            Sent = request;
            return Task.FromResult(new HttpResponse(request, new HttpHeader(), body, status));
        }

        public HttpResponse Execute(HttpRequest request) => throw new NotSupportedException();
        public void DownloadFile(string url, string fileName) => throw new NotSupportedException();
        public HttpResponse Get(HttpRequest request) => throw new NotSupportedException();
        public HttpResponse<T> Get<T>(HttpRequest request) where T : new() => throw new NotSupportedException();
        public HttpResponse Head(HttpRequest request) => throw new NotSupportedException();
        public HttpResponse Post(HttpRequest request) => throw new NotSupportedException();
        public HttpResponse<T> Post<T>(HttpRequest request) where T : new() => throw new NotSupportedException();
        public Task DownloadFileAsync(string url, string fileName) => throw new NotSupportedException();
        public Task<HttpResponse> GetAsync(HttpRequest request) => throw new NotSupportedException();
        public Task<HttpResponse<T>> GetAsync<T>(HttpRequest request) where T : new() => throw new NotSupportedException();
        public Task<HttpResponse> HeadAsync(HttpRequest request) => throw new NotSupportedException();
        public Task<HttpResponse> PostAsync(HttpRequest request) => throw new NotSupportedException();
        public Task<HttpResponse<T>> PostAsync<T>(HttpRequest request) where T : new() => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_login_refreshes_under_the_android_client(bool signedInWithPkce)
    {
        // A device login refreshed under any other client loses playback (#173).
        var endpoint = new TokenEndpoint();
        var user = new TidalUser(new OAuthTokenData { AccessToken = "old-access", RefreshToken = "refresh" }, null, signedInWithPkce, DateTime.UtcNow);

        Assert.True(await new Session(endpoint).AttemptTokenRefresh(user));

        var form = HttpUtility.ParseQueryString(Encoding.UTF8.GetString(endpoint.Sent!.ContentData));
        Assert.Equal("refresh", form["refresh_token"]);
        Assert.Equal(Globals.CLIENT_ID_PKCE, form["client_id"]);
        Assert.Equal(Globals.CLIENT_SECRET_PKCE, form["client_secret"]);
        Assert.Equal("new-access", user.AccessToken);
    }

    [Fact]
    public async Task A_rejected_refresh_keeps_the_saved_token()
    {
        var endpoint = new TokenEndpoint(HttpStatusCode.BadRequest, """{"status":400,"error":"invalid_grant","sub_status":11101}""");
        var user = new TidalUser(new OAuthTokenData { AccessToken = "old-access", RefreshToken = "refresh" }, null, false, DateTime.UtcNow);

        Assert.False(await new Session(endpoint).AttemptTokenRefresh(user));
        Assert.Equal("old-access", user.AccessToken);
    }
}
