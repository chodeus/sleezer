using System.Net;
using NzbDrone.Common.Http;

namespace Sleezer.Tests;

// Lidarr's IHttpClient answered by one handler; TidalSharp only uses the async execute path.
internal sealed class FakeHttpClient(Func<HttpRequest, HttpResponse> handler) : IHttpClient
{
    public List<HttpRequest> Requests { get; } = [];

    public Task<HttpResponse> ExecuteAsync(HttpRequest request)
    {
        Requests.Add(request);
        return Task.FromResult(handler(request));
    }

    HttpResponse IHttpClient.Execute(HttpRequest request) => throw new NotSupportedException();
    void IHttpClient.DownloadFile(string url, string fileName) => throw new NotSupportedException();
    HttpResponse IHttpClient.Get(HttpRequest request) => throw new NotSupportedException();
    HttpResponse<T> IHttpClient.Get<T>(HttpRequest request) => throw new NotSupportedException();
    HttpResponse IHttpClient.Head(HttpRequest request) => throw new NotSupportedException();
    HttpResponse IHttpClient.Post(HttpRequest request) => throw new NotSupportedException();
    HttpResponse<T> IHttpClient.Post<T>(HttpRequest request) => throw new NotSupportedException();
    Task IHttpClient.DownloadFileAsync(string url, string fileName) => throw new NotSupportedException();
    Task<HttpResponse> IHttpClient.GetAsync(HttpRequest request) => throw new NotSupportedException();
    Task<HttpResponse<T>> IHttpClient.GetAsync<T>(HttpRequest request) => throw new NotSupportedException();
    Task<HttpResponse> IHttpClient.HeadAsync(HttpRequest request) => throw new NotSupportedException();
    Task<HttpResponse> IHttpClient.PostAsync(HttpRequest request) => throw new NotSupportedException();
    Task<HttpResponse<T>> IHttpClient.PostAsync<T>(HttpRequest request) => throw new NotSupportedException();

    public static HttpResponse Respond(HttpRequest request, HttpStatusCode status, string body = "{}", string? retryAfter = null)
    {
        var headers = new HttpHeader();
        if (retryAfter != null)
            headers["Retry-After"] = retryAfter;

        return new HttpResponse(request, headers, body, status);
    }
}
