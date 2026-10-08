using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Http.Dispatchers;
using NzbDrone.Common.Http.Proxy;
using System.Net;
using NzbDrone.Plugin.Sleezer.Core.Utilities;

namespace NzbDrone.Plugin.Sleezer.Core.Replacements
{
    public class FlexibleHttpDispatcher : ManagedHttpDispatcher, IHttpDispatcher
    {
        private const string UA_PARAM = "x-user-agent";
        private const string EXT_PARAM = "x-ext";

        // Lidarr names a cover file by Path.GetExtension of its whole URL, query included,
        // so the URL has to end in the file's own extension.
        public static string WithUserAgent(string? url, string userAgent)
        {
            char separator = url?.Contains('?') == true ? '&' : '?';
            string withAgent = $"{url}{separator}{UA_PARAM}={userAgent}";
            string? extension = Path.GetExtension(url?.Split('?')[0]);
            return string.IsNullOrEmpty(extension) ? withAgent : $"{withAgent}&{EXT_PARAM}={extension}";
        }

        public FlexibleHttpDispatcher(IHttpProxySettingsProvider proxySettingsProvider,
            IUserAgentValidator userAgentValidator,
            ICreateManagedWebProxy createManagedWebProxy,
            ICertificateValidationService certificateValidationService,
            IUserAgentBuilder userAgentBuilder,
            ICacheManager cacheManager,
            Logger logger)
            : base(proxySettingsProvider, createManagedWebProxy, certificateValidationService,
                  userAgentBuilder, cacheManager, logger)
        {
            userAgentValidator.AddAllowedPattern(@"^[^/]+/\d+(\.\d+)*$");
            foreach (string pattern in blacklisted)
                userAgentValidator.AddBlacklistPattern(pattern);
        }

        private readonly string[] blacklisted =
        [
            // Fuzzy matching for Lidarr (catches variations like Lidar, Lidaar, etc.)
            ".*[a]r+.*",
            ".*[e]rr+.*",
            // Fuzzy matching for NzbDrone.Plugin.Sleezer (catches variations like Tubifary, Tubiferry, etc.)
            ".*t[uo]b?[iey]?.*",
            // Additional fuzzy blocking patterns
            ".*.[fbvd][ae]r+.*",
            ".*b[o0]t.*",
            ".*cr[ae]wl[ae]r.*",
            ".*pr[o0]xy.*",
            ".*scr[ae]p[ae]r.*"
        ];

        async Task<HttpResponse> IHttpDispatcher.GetResponseAsync(HttpRequest request, CookieContainer cookies)
        {
            ExtractUserAgentFromUrl(request);
            return await GetResponseAsync(request, cookies);
        }

        internal static void ExtractUserAgentFromUrl(HttpRequest request)
        {
            if (request.Url.Query.IsNullOrWhiteSpace()) return;

            string[] parts = request.Url.Query.Split('&');
            string? uaPart = parts.FirstOrDefault(p => p.StartsWith($"{UA_PARAM}="));

            if (uaPart != null)
            {
                string userAgent = Uri.UnescapeDataString(uaPart.Split('=')[1]);
                request.Headers.Set("User-Agent", userAgent);
                request.Url = request.Url.SetQuery(string.Join("&", parts.Where(p => p != uaPart && !p.StartsWith($"{EXT_PARAM}="))));
            }
        }

        protected override void AddRequestHeaders(HttpRequestMessage webRequest, HttpHeader headers)
        {
            string userAgent = headers.GetSingleValue("User-Agent");

            HttpHeader filtered = [];
            foreach (KeyValuePair<string, string> h in headers.Where(h => h.Key != "User-Agent"))
                filtered.Add(h.Key, h.Value);

            base.AddRequestHeaders(webRequest, filtered);

            if (userAgent != null)
            {
                webRequest.Headers.UserAgent.Clear();
                webRequest.Headers.UserAgent.ParseAdd(userAgent);
            }
        }
    }
}