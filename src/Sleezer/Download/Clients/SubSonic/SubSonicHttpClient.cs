using DownloadAssistant.Base;
using NzbDrone.Common.Http;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.SubSonic
{
    /// <summary>
    /// HTTP client wrapper for download operations
    /// Provides standardized HTTP operations with proper headers and error handling
    /// Never modifies the shared HttpClient uses individual requests with proper headers
    /// </summary>
    public class SubSonicHttpClient
    {
        private readonly System.Net.Http.HttpClient _httpClient = HttpGet.HttpClient;
        private readonly TimeSpan _timeout;
        private readonly List<IHttpRequestInterceptor> _requestInterceptors;

        /// <summary>
        /// Initializes a new instance of the SubSonicHttpClient
        /// </summary>
        /// <param name="baseUrl">Base URL for the service instance</param>
        /// <param name="requestInterceptors">Optional list of request interceptors</param>
        /// <param name="timeout">Request timeout (default: 60 seconds)</param>
        public SubSonicHttpClient(string baseUrl, IEnumerable<IHttpRequestInterceptor> requestInterceptors, TimeSpan? timeout = null)
        {
            BaseUrl = baseUrl?.TrimEnd('/') ?? throw new ArgumentNullException(nameof(baseUrl));
            _timeout = timeout ?? TimeSpan.FromSeconds(60);
            _requestInterceptors = requestInterceptors?.ToList() ?? [];
        }

        /// <summary>
        /// Gets the base URL for this service instance
        /// </summary>
        public string BaseUrl { get; }

        /// <summary>
        /// Creates a properly configured HttpRequestMessage with standard headers
        /// </summary>
        /// <param name="method">HTTP method</param>
        /// <param name="url">The URL to request (can be relative or absolute)</param>
        /// <returns>Configured HttpRequestMessage</returns>
        public HttpRequestMessage CreateRequest(HttpMethod method, string url)
        {
            string requestUrl = url.StartsWith("http") ? url : new Uri(new Uri(BaseUrl), url).ToString();

            HttpRequestMessage request = new(method, requestUrl);

            request.Headers.Add("User-Agent", SleezerPlugin.UserAgent);
            request.Headers.Add("Accept", "application/json,text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8");

            return request;
        }

        /// <summary>
        /// Sends an HTTP request with timeout handling and interceptor support
        /// </summary>
        /// <param name="request">The HTTP request to send</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>HTTP response message</returns>
        /// <exception cref="Exception">Thrown when the request fails</exception>
        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
        {
            try
            {
                HttpRequest nzbRequest = ConvertToNzbRequest(request);

                foreach (IHttpRequestInterceptor interceptor in _requestInterceptors)
                {
                    nzbRequest = interceptor.PreRequest(nzbRequest);
                }

                ApplyNzbRequestToHttpRequestMessage(nzbRequest, request);

                using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(_timeout);

                HttpResponseMessage response = await _httpClient.SendAsync(request, cts.Token);

                HttpResponse nzbResponse = await ConvertToNzbResponse(nzbRequest, response);

                foreach (IHttpRequestInterceptor interceptor in _requestInterceptors)
                {
                    nzbResponse = interceptor.PostResponse(nzbResponse);
                }

                return ConvertToHttpResponseMessage(nzbResponse);
            }
            catch (Exception ex)
            {
                throw new Exception($"HTTP request failed for URL '{request.RequestUri}': {ex.Message}", ex);
            }
        }

        private static HttpRequest ConvertToNzbRequest(HttpRequestMessage httpRequest)
        {
            HttpRequest nzbRequest = new(httpRequest.RequestUri?.ToString())
            {
                Method = httpRequest.Method
            };

            List<string> requestHeaderKeys = new();
            List<string> contentHeaderKeys = new();

            string allRequestHeaders = httpRequest.Headers.ToString();
            foreach (string line in allRequestHeaders.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                int colonIndex = line.IndexOf(':');
                if (colonIndex > 0)
                {
                    string headerName = line.Substring(0, colonIndex).Trim();
                    string headerValue = line.Substring(colonIndex + 1).Trim();
                    requestHeaderKeys.Add(headerName);
                    nzbRequest.Headers[headerName] = headerValue;
                }
            }

            if (httpRequest.Content != null)
            {
                string allContentHeaders = httpRequest.Content.Headers.ToString();
                foreach (string line in allContentHeaders.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int colonIndex = line.IndexOf(':');
                    if (colonIndex > 0)
                    {
                        string headerName = line.Substring(0, colonIndex).Trim();
                        string headerValue = line.Substring(colonIndex + 1).Trim();
                        contentHeaderKeys.Add(headerName);
                        nzbRequest.Headers[headerName] = headerValue;
                    }
                }

                byte[] contentBytes = httpRequest.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                if (contentBytes.Length > 0)
                {
                    nzbRequest.SetContent(contentBytes);
                }
            }

            nzbRequest.Cookies["__REQUEST_HEADERS__"] = string.Join("|", requestHeaderKeys);
            nzbRequest.Cookies["__CONTENT_HEADERS__"] = string.Join("|", contentHeaderKeys);

            return nzbRequest;
        }

        private static void ApplyNzbRequestToHttpRequestMessage(HttpRequest nzbRequest, HttpRequestMessage httpRequest)
        {
            HashSet<string> originalRequestHeaders = nzbRequest.Cookies.TryGetValue("__REQUEST_HEADERS__", out string? valueH)
                ? new HashSet<string>(valueH.Split('|', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            HashSet<string> originalContentHeaders = nzbRequest.Cookies.TryGetValue("__CONTENT_HEADERS__", out string? valueC)
                ? new HashSet<string>(valueC.Split('|', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach ((string key, string value) in nzbRequest.Headers)
            {
                if (originalRequestHeaders.Contains(key))
                {
                    httpRequest.Headers.Remove(key);
                    httpRequest.Headers.TryAddWithoutValidation(key, value);
                }
                else if (originalContentHeaders.Contains(key))
                {
                    if (httpRequest.Content != null)
                    {
                        httpRequest.Content.Headers.Remove(key);
                        httpRequest.Content.Headers.TryAddWithoutValidation(key, value);
                    }
                }
                else
                {
                    httpRequest.Headers.TryAddWithoutValidation(key, value);
                }
            }

            List<KeyValuePair<string, string>> actualCookies = nzbRequest.Cookies
                .Where(c => !c.Key.StartsWith("__") || !c.Key.EndsWith("__"))
                .ToList();

            if (actualCookies.Count > 0)
            {
                string cookieHeader = string.Join("; ", actualCookies.Select(c => $"{c.Key}={c.Value}"));
                httpRequest.Headers.Remove("Cookie");
                httpRequest.Headers.Add("Cookie", cookieHeader);
            }
        }

        private static async Task<HttpResponse> ConvertToNzbResponse(HttpRequest nzbRequest, HttpResponseMessage httpResponse)
        {
            HttpHeader headers = [];

            foreach (KeyValuePair<string, IEnumerable<string>> header in httpResponse.Headers)
            {
                headers[header.Key] = string.Join(", ", header.Value);
            }

            if (httpResponse.Content != null)
            {
                foreach (KeyValuePair<string, IEnumerable<string>> header in httpResponse.Content.Headers)
                {
                    headers[header.Key] = string.Join(", ", header.Value);
                }
            }

            byte[] responseData = [];
            if (httpResponse.Content != null)
            {
                responseData = await httpResponse.Content.ReadAsByteArrayAsync();
            }

            return new HttpResponse(nzbRequest, headers, responseData, httpResponse.StatusCode, httpResponse.Version);
        }

        private static HttpResponseMessage ConvertToHttpResponseMessage(HttpResponse nzbResponse)
        {
            HttpResponseMessage httpResponse = new(nzbResponse.StatusCode)
            {
                Version = nzbResponse.Version,
                Content = new ByteArrayContent(nzbResponse.ResponseData ?? [])
            };

            foreach (KeyValuePair<string, string> header in nzbResponse.Headers)
            {
                if (!httpResponse.Headers.TryAddWithoutValidation(header.Key, header.Value))
                {
                    httpResponse.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            return httpResponse;
        }

        /// <summary>
        /// Performs a GET request and returns the HttpResponseMessage
        /// </summary>
        /// <param name="url">The URL to request (can be relative or absolute)</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>HTTP response message</returns>
        /// <exception cref="Exception">Thrown when the request fails</exception>
        public async Task<HttpResponseMessage> GetAsync(string url, CancellationToken cancellationToken = default)
        {
            try
            {
                using HttpRequestMessage request = CreateRequest(HttpMethod.Get, url);
                return await SendAsync(request, cancellationToken);
            }
            catch (Exception ex)
            {
                throw new Exception($"HTTP GET request failed for URL '{url}': {ex.Message}", ex);
            }
        }
    }
}