using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeezNET;
using NLog;
using NzbDrone.Plugin.Sleezer.Core.Utilities;

namespace NzbDrone.Plugin.Sleezer.Deezer
{
    public class DeezerAPI
    {
        // Upper bound for blocking ARL init / refresh. Deezer's GW endpoint usually responds
        // in under a second; if it stalls past this, we'd rather surface a TimeoutException
        // than deadlock the indexer thread.
        private static readonly TimeSpan ArlOperationTimeout = TimeSpan.FromSeconds(30);

        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        public static DeezerAPI Instance { get; private set; } = new("");

        internal DeezerAPI(string arl)
        {
            Instance = this;
            _client = new();
            CheckAndSetARL(arl);
        }

        public DeezerClient Client => _client;

        private DeezerClient _client;
        private DateTime _lastArlUpdate;
        private string _apiToken => _client.GWApi.ActiveUserData?["checkForm"]?.ToString() ?? "null";

        internal bool CheckAndSetARL(string arl)
        {
            if (string.IsNullOrEmpty(arl))
            {
                var hasExisting = !string.IsNullOrEmpty(_client.ActiveARL);
                _logger.Debug("CheckAndSetARL called with empty ARL; existing session present={HasExisting}", hasExisting);
                return hasExisting;
            }

            // prevent double hitting the Deezer API when there's no reason to
            if (_client.ActiveARL != arl)
            {
                _logger.Debug("Setting Deezer ARL ({Fingerprint})", SecretRedactor.Fingerprint(arl));
                var startedAt = DateTime.UtcNow;
                WaitWithTimeout(_client.SetARL(arl));
                _lastArlUpdate = DateTime.Now;
                LogArlOutcome("SetARL", startedAt);
            }
            else
            {
                _logger.Trace("Deezer ARL unchanged; skipping SetARL call");
            }

            return true;
        }

        internal void TryUpdateToken()
        {
            if ((DateTime.Now - _lastArlUpdate).TotalHours >= 24)
            {
                _logger.Debug("Refreshing Deezer GW API token (>24h since last refresh)");
                var startedAt = DateTime.UtcNow;
                // refreshes the gw api token
                WaitWithTimeout(_client.SetARL(_client.ActiveARL));
                _lastArlUpdate = DateTime.Now;
                LogArlOutcome("TokenRefresh", startedAt);
            }
        }

        private void LogArlOutcome(string operation, DateTime startedAt)
        {
            var elapsedMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;
            var userData = _client.GWApi.ActiveUserData;

            // ActiveUserData stays null when Deezer silently rejects an ARL — there's no exception, just an empty session.
            // Surface that here so the indexer test failure ("Value cannot be null. (Parameter 'source')") has a real explanation in the log.
            if (userData == null)
            {
                _logger.Warn("Deezer {Operation} completed in {ElapsedMs}ms but no ActiveUserData was returned — ARL was likely rejected or expired", operation, elapsedMs);
                return;
            }

            var userId = userData["USER"]?["USER_ID"]?.ToString() ?? "?";
            var country = userData["COUNTRY"]?.ToString() ?? "?";
            var hasHq = userData["USER"]?["OPTIONS"]?["web_hq"]?.ToObject<bool?>() == true;
            var hasLossless = userData["USER"]?["OPTIONS"]?["web_lossless"]?.ToObject<bool?>() == true;
            _logger.Debug("Deezer {Operation} ok in {ElapsedMs}ms — user={UserId} country={Country} hq={Hq} lossless={Lossless}",
                operation, elapsedMs, userId, country, hasHq, hasLossless);
        }

        private static void WaitWithTimeout(Task task)
        {
            if (!task.Wait(ArlOperationTimeout))
                throw new TimeoutException($"Deezer ARL operation did not complete within {ArlOperationTimeout.TotalSeconds:F0}s");
        }

        public string GetGWUrl(string method, Dictionary<string, string>? parameters = null)
        {
            parameters ??= new();
            parameters["api_version"] = "1.0";
            parameters["api_token"] = _apiToken;
            parameters["input"] = "3";
            parameters["method"] = method;

            StringBuilder stringBuilder = new("https://www.deezer.com/ajax/gw-light.php");
            for (var i = 0; i < parameters.Count; i++)
            {
                var start = i == 0 ? "?" : "&";
                var key = WebUtility.UrlEncode(parameters.ElementAt(i).Key);
                var value = WebUtility.UrlEncode(parameters.ElementAt(i).Value);
                stringBuilder.Append(start + key + "=" + value);
            }
            return stringBuilder.ToString();
        }

        public string GetPublicUrl(string method, Dictionary<string, string>? parameters = null)
        {
            parameters ??= new();

            StringBuilder stringBuilder = new("https://api.deezer.com/" + method);
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
