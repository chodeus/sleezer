using NLog;
using NzbDrone.Common.Instrumentation;
using System.Text;
using System.Text.RegularExpressions;
using NzbDrone.Plugin.Sleezer.Download.Base;
using NzbDrone.Plugin.Sleezer.Indexers.Lucida;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.Lucida
{
    public static partial class LucidaTokenExtractor
    {
        private static readonly Logger _logger = NzbDroneLogger.GetLogger(typeof(LucidaTokenExtractor));

        public static async Task<LucidaTokens> ExtractTokensAsync(BaseHttpClient httpClient, string url)
        {
            try
            {
                string lucidaUrl = $"{httpClient.BaseUrl}/?url={Uri.EscapeDataString(url)}";
                string html = await httpClient.GetStringAsync(lucidaUrl);
                return ExtractTokensFromHtml(html);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Token extraction failed for {0}", url);
                return LucidaTokens.Empty;
            }
        }

        public static LucidaTokens ExtractTokensFromHtml(string html)
        {
            if (string.IsNullOrEmpty(html))
                return LucidaTokens.Empty;

            try
            {
                Match match = TrackTokenRegex().Match(html);
                if (!match.Success)
                {
                    _logger.Debug("No track token pattern found");
                    return LucidaTokens.Empty;
                }

                string encodedToken = match.Groups[1].Value;
                long expiry = long.TryParse(match.Groups[2].Value, out long e)
                    ? e
                    : DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();

                if (encodedToken == "album")
                {
                    _logger.Debug("Album page detected, tokens are per-track csrf values in metadata");
                    return LucidaTokens.Empty;
                }

                string decoded = DoubleBase64Decode(encodedToken);
                if (string.IsNullOrEmpty(decoded))
                    return LucidaTokens.Empty;

                return new LucidaTokens(decoded, decoded, expiry);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error extracting tokens from HTML");
                return LucidaTokens.Empty;
            }
        }

        private static string DoubleBase64Decode(string encoded)
        {
            if (string.IsNullOrEmpty(encoded))
                return string.Empty;

            try
            {
                string normalized = NormalizeBase64(encoded);
                byte[] firstBytes = Convert.FromBase64String(normalized);
                string firstDecode = Encoding.UTF8.GetString(firstBytes);

                string firstNormalized = NormalizeBase64(firstDecode);
                byte[] secondBytes = Convert.FromBase64String(firstNormalized);
                return Encoding.UTF8.GetString(secondBytes);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to double-decode token");
                return string.Empty;
            }
        }

        private static string NormalizeBase64(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            string normalized = input.Replace('-', '+').Replace('_', '/');
            int paddingNeeded = (4 - normalized.Length % 4) % 4;
            if (paddingNeeded > 0)
                normalized += new string('=', paddingNeeded);

            return normalized;
        }

        [GeneratedRegex(@"\b""?token""?\s*:\s*""([A-Za-z0-9+/=_-]{16,})""\s*,\s*\btokenExpiry\s*:\s*(\d+)", RegexOptions.Compiled)]
        private static partial Regex TrackTokenRegex();
    }
}