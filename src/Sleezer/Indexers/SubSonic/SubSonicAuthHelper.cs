using System.Text;
using NzbDrone.Common.Http;

namespace NzbDrone.Plugin.Sleezer.Indexers.SubSonic
{
    /// <summary>
    /// Helper class for SubSonic authentication
    /// Handles token generation, MD5 hashing, and URL building for secure authentication
    /// </summary>
    public static class SubSonicAuthHelper
    {
        public const string ClientName = PluginInfo.Name;
        public const string ApiVersion = "1.16.1";

        public static (string Salt, string Token) GenerateToken(string password)
        {
            string salt = GenerateSaltFromAssembly();
            string token = CalculateMd5Hash(password + salt);
            return (salt, token);
        }

        public static void AppendAuthParameters(StringBuilder urlBuilder, string username, string password, bool useTokenAuth)
        {
            string separator = urlBuilder.ToString().Contains('?') ? "&" : "?";
            urlBuilder.Append(separator).Append(LoginQuery(username, password, useTokenAuth));
        }

        /// <summary>Puts the login on a request sent through Lidarr's HTTP client; SubSonicLoginInterceptor adds it to the URL.</summary>
        public static void AttachLogin(HttpRequest request, string username, string password, bool useTokenAuth) =>
            request.Headers[SubSonicLoginInterceptor.Header] = LoginQuery(username, password, useTokenAuth);

        private static string LoginQuery(string username, string password, bool useTokenAuth)
        {
            string query = $"u={Uri.EscapeDataString(username)}&v={Uri.EscapeDataString(ApiVersion)}&c={Uri.EscapeDataString(ClientName)}";
            if (!useTokenAuth)
                return $"{query}&p={Uri.EscapeDataString(password)}";

            (string salt, string token) = GenerateToken(password);
            return $"{query}&t={token}&s={salt}";
        }

        private static string GenerateSaltFromAssembly() =>
            CalculateMd5Hash(PluginInfo.InformationalVersion + SleezerPlugin.UserAgent + SleezerPlugin.LastStarted)[..7];

        private static string CalculateMd5Hash(string input)
        {
            byte[] inputBytes = Encoding.UTF8.GetBytes(input);
            byte[] hashBytes = System.Security.Cryptography.MD5.HashData(inputBytes);
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }
    }
}