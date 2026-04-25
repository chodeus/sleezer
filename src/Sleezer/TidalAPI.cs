using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using NLog;
using NzbDrone.Common.Http;
using TidalSharp;

namespace NzbDrone.Plugin.Sleezer.Tidal
{
    public class TidalAPI
    {
        public static TidalAPI? Instance { get; private set; }

        public static void Initialize(string? configDir, IHttpClient httpClient, Logger logger)
        {
            if (Instance != null)
                return;
            Instance = new TidalAPI(configDir, httpClient, logger);
        }

        private TidalAPI(string? configDir, IHttpClient httpClient, Logger logger)
        {
            _logger = logger;
            // Pass null (not "") so TidalClient skips legacy lastUser.json
            // creation; tokens come from IPluginSettings instead.
            _client = new(string.IsNullOrEmpty(configDir) ? null : configDir, httpClient);
        }

        public TidalClient Client => _client;

        // Read live from the active session every access. Issue #42 in TrevTV's
        // upstream was caused by callers caching CountryCode across a token
        // refresh — never cache, always go through this getter.
        public string CountryCode => _client.ActiveUser?.CountryCode ?? "";
        public string SessionId => _client.ActiveUser?.SessionID ?? "";

        private readonly TidalClient _client;
        private readonly Logger _logger;

        public string GetAPIUrl(string method, Dictionary<string, string>? parameters = null)
        {
            parameters ??= new();
            parameters["sessionId"] = SessionId;
            parameters["countryCode"] = CountryCode;
            if (!parameters.ContainsKey("limit"))
                parameters["limit"] = "1000";

            StringBuilder stringBuilder = new("https://api.tidal.com/v1/");
            stringBuilder.Append(method);
            for (var i = 0; i < parameters.Count; i++)
            {
                var start = i == 0 ? "?" : "&";
                var key = WebUtility.UrlEncode(parameters.ElementAt(i).Key);
                var value = WebUtility.UrlEncode(parameters.ElementAt(i).Value);
                stringBuilder.Append(start + key + "=" + value);
            }
            return stringBuilder.ToString();
        }
    }
}
