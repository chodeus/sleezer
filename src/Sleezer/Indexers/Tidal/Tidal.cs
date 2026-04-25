using System;
using System.Collections.Generic;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Parser;
using NzbDrone.Plugin.Sleezer.Tidal;
using TidalSharp.Data;

namespace NzbDrone.Core.Indexers.Tidal
{
    public class Tidal : HttpIndexerBase<TidalIndexerSettings>
    {
        public override string Name => "Tidal";
        public override string Protocol => nameof(TidalDownloadProtocol);
        public override bool SupportsRss => false;
        public override bool SupportsSearch => true;
        public override int PageSize => 100;
        public override TimeSpan RateLimit => TimeSpan.FromSeconds(2);

        // In-flight device-authorization response between startOAuth and
        // getOAuthToken calls. Static — only one indexer auth flow can be in
        // progress globally; acceptable for a Lidarr instance.
        private static DeviceAuthorizationResponse? _pendingDeviceAuth;
        private static readonly object _authLock = new();

        // Cap a single getOAuthToken poll cycle so the Lidarr-UI HTTP request
        // doesn't tie up a server thread for the full 5-minute device-code
        // expires_in. If the user takes longer, _pendingDeviceAuth is left in
        // place so they can re-click "Authenticate" and resume polling against
        // the same device code without restarting the flow.
        private static readonly TimeSpan SingleCycleAuthBudget = TimeSpan.FromSeconds(75);

        // Tracks the access token currently loaded into TidalAPI.Instance, so
        // re-authenticating with a different account (or clearing settings)
        // forces a fresh LoadFromTokens call. Empty string = nothing loaded.
        private static string _loadedAccessToken = string.Empty;

        public Tidal(IHttpClient httpClient,
            IIndexerStatusService indexerStatusService,
            IConfigService configService,
            IParsingService parsingService,
            Logger logger)
            : base(httpClient, indexerStatusService, configService, parsingService, logger)
        {
        }

        public override IIndexerRequestGenerator GetRequestGenerator()
        {
            EnsureApiInitialized();
            EnsureTokensLoaded();

            if (TidalAPI.Instance?.Client.ActiveUser == null)
                return null!;

            return new TidalRequestGenerator
            {
                Settings = Settings,
                Logger = _logger
            };
        }

        public override IParseIndexerResponse GetParser()
        {
            return new TidalParser
            {
                Settings = Settings,
                Logger = _logger
            };
        }

        public override object RequestAction(string action, IDictionary<string, string> query)
        {
            switch (action)
            {
                case "startOAuth":
                    return StartOAuth(query);
                case "getOAuthToken":
                    return GetOAuthToken();
                default:
                    return base.RequestAction(action, query);
            }
        }

        private object StartOAuth(IDictionary<string, string> query)
        {
            EnsureApiInitialized();
            try
            {
                var auth = TidalAPI.Instance!.Client.BeginDeviceLogin().GetAwaiter().GetResult();
                lock (_authLock)
                {
                    _pendingDeviceAuth = auth;
                }

                string tidalUrl = auth.VerificationUriComplete;
                if (!tidalUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !tidalUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    tidalUrl = "https://" + tidalUrl;

                _logger.Info("Tidal device authorization started; user code {UserCode}, expires in {Expires}s", auth.UserCode, auth.ExpiresIn);

                // Lidarr's FieldType.OAuth popup expects to navigate to its
                // own /oauth.html callback to fire onCompleteOauth in the
                // parent window. Tidal's device-code flow has no redirect,
                // so we hand Lidarr a data: URL whose content shows the
                // verification link plus an "I've Authorized" button that
                // navigates the popup to /oauth.html?status=ready, which
                // triggers Lidarr's standard callback wiring → getOAuthToken.
                query.TryGetValue("callbackUrl", out string? callbackUrl);
                string oauthUrl = BuildPopupBridgeUrl(tidalUrl, auth.UserCode, callbackUrl);

                return new
                {
                    OauthUrl = oauthUrl,
                    userCode = auth.UserCode,
                    expiresIn = auth.ExpiresIn
                };
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Tidal device authorization failed to start");
                return new { error = "Could not start Tidal authorization. See log for details." };
            }
        }

        // Builds an inline data: URL that the Lidarr OAuth popup loads. The
        // page shows the Tidal verification URL prominently and provides an
        // "I've Authorized" button which navigates the popup to Lidarr's own
        // oauth.html callback so the standard onCompleteOauth → getOAuthToken
        // chain fires. Lets us reuse FieldType.OAuth despite Tidal not
        // redirecting back to a Lidarr-controlled URL after device-code auth.
        private static string BuildPopupBridgeUrl(string tidalUrl, string userCode, string? callbackUrl)
        {
            string safeTidalUrl = System.Net.WebUtility.HtmlEncode(tidalUrl);
            string safeUserCode = System.Net.WebUtility.HtmlEncode(userCode ?? string.Empty);

            // Fall back to a no-op callback if Lidarr didn't pass one (defensive
            // — current Lidarr always does, but a future UI variant might not).
            string safeCallback = string.IsNullOrEmpty(callbackUrl)
                ? "about:blank"
                : System.Net.WebUtility.HtmlEncode(callbackUrl);

            string html = $$"""
                <!DOCTYPE html>
                <html lang="en">
                <head>
                  <meta charset="utf-8">
                  <title>Tidal Authorization</title>
                  <meta name="viewport" content="width=device-width, initial-scale=1">
                  <style>
                    :root { color-scheme: dark; }
                    body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
                           max-width: 520px; margin: 2em auto; padding: 1em 1.5em;
                           background: #1a1a1a; color: #eee; line-height: 1.5; }
                    h2 { margin-top: 0; }
                    ol { padding-left: 1.4em; }
                    li { margin: 0.4em 0; }
                    .row { text-align: center; margin: 1em 0; }
                    .code { display: inline-block; font-family: ui-monospace, Menlo, monospace;
                            font-size: 1.6em; background: #333; padding: 0.4em 0.9em;
                            border-radius: 6px; letter-spacing: 0.18em; }
                    a.btn, button.btn { display: inline-block; padding: 0.65em 1.4em;
                            margin: 0.3em; border-radius: 6px; cursor: pointer;
                            text-decoration: none; font-size: 1em; border: 0; }
                    .primary { background: #00d49f; color: #000; }
                    .primary:hover { background: #00b287; }
                    .secondary { background: #2a2a2a; color: #eee; border: 1px solid #444; }
                    .secondary:hover { background: #333; }
                    small { color: #888; }
                  </style>
                </head>
                <body>
                  <h2>Authorize Tidal</h2>
                  <ol>
                    <li>Click the button to open Tidal in a new tab.</li>
                    <li>Log in with your Tidal account if prompted.</li>
                    <li>Wait until Tidal confirms &ldquo;Device linked&rdquo;.</li>
                    <li>Come back here and click <strong>I&rsquo;ve Authorized</strong>.</li>
                  </ol>
                  <div class="row">
                    <a class="btn secondary" href="{{safeTidalUrl}}" target="_blank" rel="noopener">Open Tidal &rarr;</a>
                  </div>
                  <p style="text-align:center;">If Tidal asks for a code, enter:</p>
                  <div class="row"><span class="code">{{safeUserCode}}</span></div>
                  <div class="row">
                    <button class="btn primary" id="auth-done" data-callback="{{safeCallback}}">I&rsquo;ve Authorized</button>
                  </div>
                  <p style="text-align:center;"><small>This window closes automatically once Lidarr has your tokens.</small></p>
                  <script>
                    document.getElementById("auth-done").addEventListener("click", function () {
                      // data-callback already had HTML entities decoded by the parser,
                      // so this is the raw URL — safe to concatenate query parts.
                      var cb = this.getAttribute("data-callback");
                      var sep = cb.indexOf("?") >= 0 ? "&" : "?";
                      window.location.href = cb + sep + "status=ready";
                    });
                  </script>
                </body>
                </html>
                """;

            return "data:text/html;charset=utf-8," + Uri.EscapeDataString(html);
        }

        private object GetOAuthToken()
        {
            DeviceAuthorizationResponse? auth;
            lock (_authLock)
            {
                auth = _pendingDeviceAuth;
            }

            if (auth == null)
                return new { error = "Click Authenticate first to start the Tidal login flow." };

            // Cap how long we block on the device-token poll. If the user
            // hasn't authorized yet by the time we hit the budget, return a
            // pending status WITHOUT clearing _pendingDeviceAuth so the next
            // click resumes against the same device_code.
            using var budgetCts = new CancellationTokenSource(SingleCycleAuthBudget);

            try
            {
                var data = TidalAPI.Instance!.Client
                    .CompleteDeviceLogin(auth, budgetCts.Token)
                    .GetAwaiter().GetResult();

                lock (_authLock)
                {
                    _pendingDeviceAuth = null;
                }

                var user = TidalAPI.Instance.Client.ActiveUser!;
                _loadedAccessToken = user.AccessToken;
                _logger.Info("Tidal device login complete for user {UserId} ({CountryCode})", user.UserId, user.CountryCode);

                return new
                {
                    accessToken = user.AccessToken,
                    refreshToken = user.RefreshToken,
                    tokenType = user.TokenType,
                    expires = DateTime.UtcNow.AddSeconds(data.ExpiresIn),
                    countryCode = user.CountryCode,
                    userId = user.UserId
                };
            }
            catch (OperationCanceledException) when (budgetCts.IsCancellationRequested)
            {
                _logger.Info("Tidal device-code poll budget elapsed; click Authenticate again to resume.");
                return new { error = "Still waiting for browser login. Click Authenticate again to resume polling." };
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Tidal device login did not complete");
                lock (_authLock)
                {
                    _pendingDeviceAuth = null;
                }
                return new { error = ex.Message };
            }
        }

        private void EnsureApiInitialized()
        {
            if (TidalAPI.Instance == null)
            {
                // Pass null (not ""): TidalClient ctor uses the path to create
                // the legacy lastUser.json directory, and CreateDirectory("")
                // throws ArgumentException. We don't use the file-based store
                // — tokens come from Settings.
                TidalAPI.Initialize(null, _httpClient, _logger);
            }
        }

        private void EnsureTokensLoaded()
        {
            // Settings cleared (no auth yet) — nothing to load. Drop any
            // previously-loaded session so a stale ActiveUser doesn't keep
            // serving requests after the user wipes their tokens.
            if (string.IsNullOrEmpty(Settings.AccessToken))
            {
                if (_loadedAccessToken.Length > 0)
                {
                    _logger.Debug("Tidal settings cleared; dropping in-memory session.");
                    TidalAPI.Instance!.Client.ActiveUser = null;
                    _loadedAccessToken = string.Empty;
                }
                return;
            }

            // Already loaded the same token — short-circuit. If the user
            // re-authenticates with a different account, AccessToken changes
            // and we reload.
            if (string.Equals(_loadedAccessToken, Settings.AccessToken, StringComparison.Ordinal))
                return;

            try
            {
                TidalAPI.Instance!.Client.LoadFromTokens(
                    Settings.AccessToken,
                    Settings.RefreshToken,
                    Settings.TokenType,
                    Settings.UserId,
                    Settings.Expires).GetAwaiter().GetResult();
                _loadedAccessToken = Settings.AccessToken;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to restore Tidal session from saved tokens; user must re-authenticate");
            }
        }
    }
}
