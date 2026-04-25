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

                // Defensive: Tidal's device_authorization response always
                // includes verificationUriComplete in practice, but a future
                // API change or proxy mangling could omit it.
                string tidalUrl = auth.VerificationUriComplete ?? string.Empty;
                if (string.IsNullOrEmpty(tidalUrl))
                    throw new InvalidOperationException("Tidal device_authorization response missing verificationUriComplete.");

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

        // GitHub Pages URL serving the OAuth bridge HTML from docs/auth-bridge.html
        // in this repo. Pages is required (not jsDelivr / statically.io / raw.github)
        // because those CDNs proxy GitHub's text/plain content-type, which makes
        // browsers display the page as plain text instead of rendering it. Pages
        // is the only option that serves .html files with text/html.
        //
        // We also can't use a data: URL — Chrome (and Firefox) block top-level
        // navigation to data: URLs as a phishing mitigation since Chrome 60,
        // which manifests as window.open() returning a window whose body never
        // renders.
        private const string BridgePageBase = "https://chodeus.github.io/sleezer/auth-bridge.html";

        // Builds the URL Lidarr's OAuth popup navigates to. The bridge page
        // shows the Tidal verification URL + user_code, and on click of its
        // "I've Authorized" button it navigates the popup to Lidarr's own
        // /oauth.html callback so the standard onCompleteOauth →
        // getOAuthToken chain fires. Lets us reuse FieldType.OAuth despite
        // Tidal not redirecting back to a Lidarr-controlled URL after
        // device-code auth.
        private static string BuildPopupBridgeUrl(string tidalUrl, string userCode, string? callbackUrl)
        {
            string safeCallback = callbackUrl ?? string.Empty;

            string query = "?tidalUrl=" + Uri.EscapeDataString(tidalUrl)
                         + "&userCode=" + Uri.EscapeDataString(userCode ?? string.Empty)
                         + "&callbackUrl=" + Uri.EscapeDataString(safeCallback);

            return BridgePageBase + query;
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
                    Settings.Expires,
                    Settings.CountryCode).GetAwaiter().GetResult();
                _loadedAccessToken = Settings.AccessToken;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to restore Tidal session from saved tokens; user must re-authenticate");
            }
        }
    }
}
