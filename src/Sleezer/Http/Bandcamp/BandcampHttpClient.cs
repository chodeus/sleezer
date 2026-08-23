using System;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download.Clients.Bandcamp;

namespace NzbDrone.Core.Http.Bandcamp
{
    /// <summary>
    /// HTTP client wrapper for Bandcamp requests with rate limiting,
    /// browser-like user-agent, cookie injection, and credential-safe logging.
    /// </summary>
    public class BandcampHttpClient
    {
        private const string BrowserUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

        private const string BrowserAccept =
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";

        private static readonly TimeSpan RateLimitInterval = TimeSpan.FromSeconds(2);

        // Long enough for a large discography archive, short enough that a wedged
        // connection does not hold a queue slot forever.
        private static readonly TimeSpan DownloadRequestTimeout = TimeSpan.FromMinutes(30);

        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        public BandcampHttpClient(IHttpClient httpClient, Logger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        /// <summary>
        /// Creates an HttpRequestBuilder pre-configured for Bandcamp with browser-like
        /// headers, cookie injection, and rate limiting.
        /// </summary>
        /// <param name="url">Full URL to request.</param>
        /// <param name="cookies">Raw cookie string from browser (e.g. "identity=xxx; js=1").</param>
        public HttpRequestBuilder CreateRequestBuilder(string url, string cookies)
        {
            // Every credentialed Bandcamp request funnels through here, so the
            // destination is vouched for once, in one place, before the Cookie header
            // is attached. Callers used to pass response-derived URLs unchecked.
            if (!IsCredentialedBandcampUrl(url))
            {
                // Formatted from the parse result, not by re-parsing: the input being
                // rejected is exactly the input that may not parse.
                var destination = Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed.GetLeftPart(UriPartial.Authority) : "an unparseable URL";
                throw new BandcampCollectionException($"Refusing to send Bandcamp session cookies to {destination}.");
            }

            var builder = new HttpRequestBuilder(url)
            {
                RateLimit = RateLimitInterval
            };

            // Set RateLimitKey via PostProcess since HttpRequestBuilder doesn't expose it directly
            builder.PostProcess = req => req.RateLimitKey = "bandcamp";

            // Browser-like headers so Bandcamp serves full pages
            builder.Headers.Set("User-Agent", BrowserUserAgent);
            builder.Headers.Set("Accept", BrowserAccept);
            builder.Headers.Set("Accept-Language", "en-US,en;q=0.9");

            // Inject cookies via the Cookie header (not via builder.Cookies dictionary)
            // so they're sent as-is without per-cookie parsing.
            // Users may paste just the identity cookie value, or the full cookie string.
            // If it doesn't contain '=', assume it's just the identity value.
            var cookieHeader = NormalizeCookieHeader(cookies);
            if (!string.IsNullOrWhiteSpace(cookieHeader))
            {
                builder.Headers.Set("Cookie", cookieHeader);
            }

            return builder;
        }

        // Bandcamp serves pages from bandcamp.com and files from its bcbits CDN. Matched
        // on a label boundary: EndsWith("bandcamp.com") would accept evilbandcamp.com.
        public static bool IsCredentialedBandcampUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return false;

            if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                return false;

            return uri.Host.Equals("bandcamp.com", StringComparison.OrdinalIgnoreCase)
                   || uri.Host.EndsWith(".bandcamp.com", StringComparison.OrdinalIgnoreCase)
                   || uri.Host.EndsWith(".bcbits.com", StringComparison.OrdinalIgnoreCase);
        }

        public static string NormalizeCookieHeader(string cookies)
        {
            if (string.IsNullOrWhiteSpace(cookies))
            {
                return string.Empty;
            }

            var cookieHeader = cookies.Trim();
            return cookieHeader.Contains('=') ? cookieHeader : $"identity={cookieHeader}";
        }

        /// <summary>
        /// Executes an HTTP GET request and returns the raw HttpResponse.
        /// Use .Content on the response to access the string body.
        /// Logs URL and status but never logs cookie values.
        /// </summary>
        public async Task<HttpResponse> ExecuteAsync(HttpRequest request)
        {
            // Log the request URL without any headers (cookies are in headers)
            _logger.Debug("Bandcamp request: {0} {1}", request.Method, request.Url);

            try
            {
                var response = await _httpClient.ExecuteAsync(request);

                _logger.Debug("Bandcamp response: {0} -> {1} ({2} bytes)",
                    request.Url,
                    (int)response.StatusCode,
                    response.Content?.Length ?? 0);

                return response;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Bandcamp request failed: {0}", request.Url);
                throw;
            }
        }

        /// <summary>
        /// Executes an HTTP request and returns the raw response (for non-string content).
        /// </summary>
        public async Task<HttpResponse> ExecuteRawAsync(HttpRequest request, CancellationToken cancellationToken = default)
        {
            // Lidarr's IHttpClient exposes no CancellationToken, so a transfer already in
            // flight cannot be interrupted through it. What is available: refuse to start
            // one that is already cancelled, and bound the request so it cannot hang.
            cancellationToken.ThrowIfCancellationRequested();

            if (request.RequestTimeout == default)
            {
                request.RequestTimeout = DownloadRequestTimeout;
            }

            _logger.Debug("Bandcamp request: {0} {1}", request.Method, request.Url);

            try
            {
                var response = await _httpClient.ExecuteAsync(request);

                _logger.Debug("Bandcamp response: {0} -> {1}",
                    request.Url,
                    (int)response.StatusCode);

                return response;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Bandcamp request failed: {0}", request.Url);
                throw;
            }
        }
    }
}
