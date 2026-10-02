using NzbDrone.Common.Http;

namespace NzbDrone.Plugin.Sleezer.Indexers.SubSonic
{
    /// <summary>Moves a SubSonic login carried in a request header into the URL at send time.</summary>
    // Lidarr logs indexer request URLs at debug before interceptors run, and its log cleaner doesn't know Subsonic's u, t, s and p.
    public class SubSonicLoginInterceptor : IHttpRequestInterceptor
    {
        public const string Header = "X-Sleezer-SubSonic-Login";

        public HttpRequest PreRequest(HttpRequest request)
        {
            string? login = request.Headers.GetSingleValue(Header);
            if (string.IsNullOrEmpty(login))
                return request;

            request.Headers.Remove(Header);
            request.Url = request.Url.SetQuery(string.IsNullOrEmpty(request.Url.Query) ? login : $"{request.Url.Query}&{login}");
            return request;
        }

        public HttpResponse PostResponse(HttpResponse response) => response;
    }
}
