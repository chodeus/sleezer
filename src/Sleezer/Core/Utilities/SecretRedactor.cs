using System;
using System.Text.RegularExpressions;

namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    public static class SecretRedactor
    {
        // Match well-known sensitive query-string params so a logged URL doesn't leak the secret.
        private static readonly Regex SensitiveQueryParam = new(
            @"(?<=[?&](?:api_token|arl|sid|access_token|refresh_token|apikey|api_key)=)[^&\s]+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static string Fingerprint(string? secret)
        {
            if (string.IsNullOrEmpty(secret))
                return "<empty>";

            var len = secret.Length;
            if (len <= 8)
                return $"<len={len}>";

            return $"{secret[..4]}…{secret[^4..]} (len={len})";
        }

        public static string RedactUrl(string? url)
        {
            if (string.IsNullOrEmpty(url))
                return string.Empty;

            return SensitiveQueryParam.Replace(url, "<redacted>");
        }
    }
}
