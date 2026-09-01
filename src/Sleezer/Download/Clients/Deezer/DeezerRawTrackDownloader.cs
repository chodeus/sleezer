using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeezNET.Data;
using Newtonsoft.Json.Linq;
using NzbDrone.Core.Download.Clients.Deezer.Queue;
using NzbDrone.Plugin.Sleezer.Core.Deezer;
using NzbDrone.Plugin.Sleezer.Deezer;

namespace NzbDrone.Core.Download.Clients.Deezer
{
    // Track tokens expire in ~10 minutes; the caller refreshes the token and retries once.
    public class DeezerUrlExpiredException : Exception
    {
        public DeezerUrlExpiredException(string message) : base(message)
        {
        }
    }

    /// <summary>Streams a Deezer track to disk with correct stripe decoding — see DeezerStreamDecoder.</summary>
    public static class DeezerRawTrackDownloader
    {
        private const string GetUrlEndpoint = "https://media.deezer.com/v1/get_url";

        // With ResponseHeadersRead the HttpClient timeout stops at the headers; this caps a stalled body read.
        private static readonly TimeSpan PerTrackTimeout = TimeSpan.FromMinutes(30);

        private static readonly HttpClient _client = new();

        public static async Task DownloadAsync(long trackId, string trackToken, string outPath, Bitrate bitrate, CancellationToken token = default)
        {
            using var stallCap = CancellationTokenSource.CreateLinkedTokenSource(token);
            stallCap.CancelAfter(PerTrackTimeout);

            try
            {
                await DownloadCoreAsync(trackId, trackToken, outPath, bitrate, stallCap.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // Not TaskCanceledException — DoDownload treats that as queue removal, not failure.
                throw new TimeoutException($"Deezer track {trackId} stalled for over {PerTrackTimeout.TotalMinutes:F0} minutes; aborting.");
            }
        }

        private static async Task DownloadCoreAsync(long trackId, string trackToken, string outPath, Bitrate bitrate, CancellationToken token)
        {
            var url = await GetTrackUrlAsync(trackId, trackToken, bitrate, token);
            var isEncrypted = url.AbsoluteUri.Contains("/mobile/") || url.AbsoluteUri.Contains("/media/");
            var blowfishKey = DeezerStreamDecoder.GenerateBlowfishKey(trackId.ToString(CultureInfo.InvariantCulture));

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                throw new DeezerUrlExpiredException($"Deezer CDN rejected the media URL for track {trackId} (HTTP 403) — track token likely expired.");
            response.EnsureSuccessStatusCode();

            await using var body = await response.Content.ReadAsStreamAsync(token);
            await using var file = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await DeezerStreamDecoder.DecodeAsync(body, file, isEncrypted, blowfishKey, token);
        }

        private static async Task<Uri> GetTrackUrlAsync(long trackId, string trackToken, Bitrate bitrate, CancellationToken token)
        {
            var client = DeezerAPI.Instance.Client;
            var options = client.GWApi.ActiveUserData?["USER"]?["OPTIONS"];
            var licenseToken = options?["license_token"]?.ToString();
            if (string.IsNullOrEmpty(licenseToken))
                throw new InvalidOperationException("No Deezer license token available — the ARL session is not initialized or was rejected.");

            // Pre-flight from account options (deezer-py parity); the media API still enforces server-side.
            var lossless = options?["web_lossless"]?.ToObject<bool?>() == true || options?["mobile_lossless"]?.ToObject<bool?>() == true;
            var hq = lossless || options?["web_hq"]?.ToObject<bool?>() == true || options?["mobile_hq"]?.ToObject<bool?>() == true;
            if (bitrate == Bitrate.FLAC && !lossless)
                throw new InsufficientLicenseRightsException($"Deezer account has no lossless streaming — cannot download track {trackId} as FLAC. A Premium/HiFi ARL is required.");
            if (bitrate == Bitrate.MP3_320 && !hq)
                throw new InsufficientLicenseRightsException($"Deezer account has no high-quality streaming — cannot download track {trackId} as MP3 320.");

            var requestBody = new JObject
            {
                ["license_token"] = licenseToken,
                ["track_tokens"] = new JArray(trackToken),
                ["media"] = new JArray(new JObject
                {
                    ["type"] = "FULL",
                    ["formats"] = new JArray(new JObject
                    {
                        ["cipher"] = "BF_CBC_STRIPE",
                        ["format"] = bitrate.ToString(),
                    }),
                }),
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, GetUrlEndpoint)
            {
                Content = new StringContent(requestBody.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("Cookie", "arl=" + client.ActiveARL);

            using var response = await _client.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
            var json = JObject.Parse(await response.Content.ReadAsStringAsync(token));

            // Keep the message verbatim — DownloadItem's classifier matches on it.
            var topError = (json["errors"] as JArray)?.FirstOrDefault();
            if (topError != null)
                throw ErrorToException(topError);

            var data = (json["data"] as JArray)?.FirstOrDefault();
            var itemError = (data?["errors"] as JArray)?.FirstOrDefault();
            if (itemError != null)
            {
                // Code 2002 is wrong-geolocation (deezer-py's mapping).
                if (itemError["code"]?.Value<int>() == 2002)
                    throw new GeoRestrictionException($"Track {trackId} is not available in your country (code 2002).");
                throw ErrorToException(itemError);
            }

            var sourceUrl = data?["media"]?.FirstOrDefault()?["sources"]?.FirstOrDefault()?["url"]?.ToString();
            if (string.IsNullOrEmpty(sourceUrl))
                throw new TrackUnavailableException($"Deezer reports no media sources for track {trackId} at {bitrate} (removed from catalog, or region-locked).");

            return new Uri(sourceUrl);
        }

        // "License token has no sufficient rights" contains "token" but neither "expired" nor "invalid".
        private static Exception ErrorToException(JToken error)
        {
            var message = error["message"]?.ToString() ?? error.ToString();
            if (message.Contains("token", StringComparison.OrdinalIgnoreCase)
                && (message.Contains("expired", StringComparison.OrdinalIgnoreCase) || message.Contains("invalid", StringComparison.OrdinalIgnoreCase)))
                return new DeezerUrlExpiredException(message);
            return new InvalidOperationException(message);
        }
    }
}
