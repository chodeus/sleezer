using System.Net;
using NzbDrone.Common.Http;

namespace TidalSharp;

// The one retry policy for Tidal's API and its CDN: a rate limit, a 5xx or a dropped connection gets a bounded retry.
internal static class TransientHttp
{
    public const int MaxAttempts = 4;
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);

    public static async Task<HttpResponse> SendAsync(IHttpClient client, HttpRequestBuilder request, CancellationToken token)
    {
        for (int attempt = 1; ; attempt++)
        {
            HttpResponse response;
            try
            {
                response = await client.ProcessRequestAsync(request);
            }
            catch (Exception ex) when (attempt < MaxAttempts && !token.IsCancellationRequested && ex is WebException or HttpRequestException or IOException)
            {
                await Task.Delay(Backoff(attempt), token);
                continue;
            }

            if (attempt >= MaxAttempts || !IsTransient(response.StatusCode))
                return response;

            await Task.Delay(Delay(attempt, response), token);
        }
    }

    public static bool IsTransient(HttpStatusCode status) =>
        status == HttpStatusCode.TooManyRequests || (int)status >= 500;

    // A Retry-After header wins, clamped to [0, 60s]; otherwise 1s, 2s, 4s.
    public static TimeSpan Delay(int attempt, HttpResponse response)
    {
        if (!response.Headers.ContainsKey("Retry-After"))
            return Backoff(attempt);

        TimeSpan retryAfter = new TooManyRequestsException(response.Request, response).RetryAfter;
        return retryAfter < TimeSpan.Zero ? TimeSpan.Zero : retryAfter > MaxDelay ? MaxDelay : retryAfter;
    }

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
}
