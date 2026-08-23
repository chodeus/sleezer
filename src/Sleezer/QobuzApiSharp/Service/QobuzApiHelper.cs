// Vendored from DaveBinM/QobuzApiSharp (GPL-3.0), kept structurally as-is so
// upstream fixes can be pulled by hand. Nullable is off for the same reason.
#nullable disable
using Newtonsoft.Json;
using QobuzApiSharp.Exceptions;
using QobuzApiSharp.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace QobuzApiSharp.Service
{
    /// <summary>
    /// An internal helper class for various internal tooling functions.
    /// </summary>
    internal static class QobuzApiHelper
    {
        private static string CachedBundleString;

        /// <summary>
        /// Fetches the bundle.js string from the Qobuz Web Player.
        /// </summary>
        private static void FetchBundleString()
        {
            using (HttpClient QobuzWebClient = new HttpClient())
            {
                // Wait() + Result would block twice and surface faults as AggregateException;
                // this stays synchronous because Lidarr's indexer contract is.
                string bundleHTML = QobuzWebClient
                    .GetStringAsync($"{QobuzApiConstants.WEB_PLAYER_BASE_URL}/login")
                    .ConfigureAwait(false).GetAwaiter().GetResult();

                try
                {
                    // Grab link to bundle.js
                    string bundleSuffix = Regex.Match(bundleHTML, "<script src=\"(?<bundleJS>\\/resources\\/\\d+\\.\\d+\\.\\d+-[a-z]\\d{3}\\/bundle\\.js)").Groups[1].Value;
                    CachedBundleString = QobuzWebClient
                        .GetStringAsync($"{QobuzApiConstants.WEB_PLAYER_BASE_URL}{bundleSuffix}")
                        .ConfigureAwait(false).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    // If obtaining bundle.js info fails, throw error.
                    throw new ApiErrorResponseException("Failed to download bundje.js.", ex);
                }
            }
        }

        /// <summary>
        /// Gets the Qobuz Web Player app_id. Valid as of bundle-8.1.0-b019.js.
        /// </summary>
        /// <returns>The production app_id string</returns>
        internal static string GetWebPlayerAppId()
        {
            if (CachedBundleString == null)
            {
                FetchBundleString();
            }

            // The production app_id is found in the production api config block.
            return Regex.Match(CachedBundleString, "production:\\{api:\\{appId:\"(\\d+)\"").Groups[1].Value;
        }

        /// <summary>
        /// Gets the Qobuz Web Player app_secret. Valid as of bundle-8.1.0-b019.js.
        /// </summary>
        /// <returns>A string.</returns>
        internal static string GetWebPlayerAppSecret()
        {
            if (CachedBundleString == null)
            {
                FetchBundleString();
            }

            // The app_secret is derived from a seed embedded in the bundle's initialization() function,
            // combined with Berlin timezone info/extras from the timezones data table.
            // Formula: Base64Decode((seed + berlin.info + berlin.extras).Substring(0, combined.Length - 44))
            // This matches what window.rng.prototype.initialization() computes at runtime for production.
            string seed = Regex.Match(CachedBundleString, "initialSeed\\(\"([^\"]+)\",window\\.utimezone\\.berlin\\)").Groups[1].Value;
            var berlinMatch = Regex.Match(CachedBundleString, "name:\"Europe/Berlin\",info:\"([^\"]+)\",extras:\"([^\"]+)\"");
            string berlinInfo = berlinMatch.Groups[1].Value;
            string berlinExtras = berlinMatch.Groups[2].Value;

            string combined = seed + berlinInfo + berlinExtras;

            // A changed bundle shows up here as a short string. Indexing blindly threw
            // ArgumentOutOfRangeException, which says nothing about the real cause.
            if (string.IsNullOrEmpty(seed) || combined.Length <= 44)
            {
                throw new ApiErrorResponseException("Could not derive the Qobuz app_secret: the web player's bundle.js no longer matches the expected format.");
            }

            string substr = combined.Substring(0, combined.Length - 44);

            // Ensure proper base64 padding before decoding
            int pad = substr.Length % 4;
            if (pad != 0)
                substr += new string('=', 4 - pad);

            return Encoding.UTF8.GetString(Convert.FromBase64String(substr));
        }

        /// <summary>
        /// Method and URI only. HttpRequestMessage.ToString() renders headers, and
        /// SendAsync sets X-User-Auth-Token, so the full render puts the caller's
        /// credential onto every API exception.
        /// </summary>
        internal static string DescribeRequest(HttpRequestMessage request)
            => request == null ? string.Empty : $"{request.Method} {request.RequestUri?.GetLeftPart(UriPartial.Path)}";

        /// <summary>
        /// Deserializes the response.
        /// </summary>
        /// <typeparam name="T">Expected result object type</typeparam>
        /// <param name="response">The response.</param>
        /// <returns>The deserialized object from the JSON string.</returns>
        internal static T DeserializeResponse<T>(HttpResponseMessage response)
        {
            string jsonResultString = "";

            try
            {
                jsonResultString = response.Content.ReadAsStringAsync().ConfigureAwait(false).GetAwaiter().GetResult();
                return JsonConvert.DeserializeObject<T>(jsonResultString);
            }
            catch (Exception ex)
            {
                throw new ApiResponseParseErrorException($"Failed to parse API response for type {typeof(T).Name}.", jsonResultString, ex);
            }
        }


        /// <summary>
        /// Generates the file url request signature.
        /// </summary>
        /// <param name="format_id">The format_id.</param>
        /// <param name="track_id">The track_id.</param>
        /// <param name="timestamp">The timestamp.</param>
        /// <param name="app_secret">The app_secret.</param>
        /// <returns>A string.</returns>
        internal static string GenerateFileUrlRequestSignature(string format_id, string track_id, string timestamp, string app_secret)
        {
            string dataToSign = String.Concat("trackgetFileUrlformat_id", format_id, "intentstreamtrack_id", track_id, timestamp, app_secret);

            using (var md5Hash = MD5.Create())
            {
                return MD5Utilities.GetMd5Hash(md5Hash, dataToSign);
            }
        }

        /// <summary>
        /// Create a query string with provided key/value parameters.
        /// </summary>
        /// <param name="parameters">The parameters.</param>
        /// <returns>A string.</returns>
        internal static string ToQueryString(IDictionary<string, string> parameters)
        {
            var array = parameters
                // Null means "not supplied" — every optional endpoint parameter defaults to
                // it. An explicit "" is a caller asking for key=, which Qobuz may read
                // differently from omission, so only null is dropped.
                .Where(kv => kv.Value != null)
                .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}")
                .ToArray();

            return string.Join("&", array);
        }

    }
}