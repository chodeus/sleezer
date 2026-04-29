using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using NzbDrone.Plugin.Sleezer.Tidal;
using TidalSharp;
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

        private readonly IIndexerRepository _indexerRepository;

        public Tidal(IHttpClient httpClient,
            IIndexerStatusService indexerStatusService,
            IIndexerRepository indexerRepository,
            IConfigService configService,
            IParsingService parsingService,
            Logger logger)
            : base(httpClient, indexerStatusService, configService, parsingService, logger)
        {
            _indexerRepository = indexerRepository;
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

            _logger.Debug("Loading Tidal session from saved tokens — access={Access} refresh={Refresh} expires={Expires:o} country={Country}",
                SecretRedactor.Fingerprint(Settings.AccessToken),
                SecretRedactor.Fingerprint(Settings.RefreshToken),
                Settings.Expires,
                Settings.CountryCode);

            try
            {
                TidalAPI.Instance!.Client.LoadFromTokens(
                    Settings.AccessToken,
                    Settings.RefreshToken,
                    Settings.TokenType,
                    Settings.UserId,
                    Settings.Expires,
                    Settings.CountryCode,
                    onTokensRefreshed: PersistRefreshedTokens).GetAwaiter().GetResult();
                _loadedAccessToken = Settings.AccessToken;
                var loaded = TidalAPI.Instance!.Client.ActiveUser;
                if (loaded != null)
                    _logger.Debug("Tidal session loaded — user={UserId} country={Country} type={Type}",
                        loaded.UserId, loaded.CountryCode, loaded.TokenType);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to restore Tidal session from saved tokens; user must re-authenticate");
            }
        }

        // Fired by TidalSharp from inside any successful AttemptTokenRefresh
        // (whether triggered by the search FetchPage override below or by the
        // download path's API.Call). Without this, refreshes only live in the
        // singleton TidalAPI's in-memory state — Lidarr's settings DB keeps
        // serving the original (eventually-dead) token to the next process
        // restart, and the user has to re-authenticate every ~24h when the
        // saved access_token expires for real.
        //
        // Settings is `(TSettings)Definition.Settings`, so mutating Settings
        // here mutates the same object SetFields serialises out.
        private void PersistRefreshedTokens(TidalUser user)
        {
            try
            {
                Settings.AccessToken = user.AccessToken;
                Settings.RefreshToken = user.RefreshToken;
                Settings.TokenType = user.TokenType;
                Settings.Expires = user.ExpirationDate;
                Settings.UserId = user.UserId;
                if (!string.IsNullOrEmpty(user.CountryCode))
                    Settings.CountryCode = user.CountryCode;

                _loadedAccessToken = user.AccessToken;

                // Definition.Id == 0 during the indexer Test flow before the
                // record is saved; SetFields would no-op or throw. Skip it
                // — the Test flow doesn't need persistence anyway.
                if (Definition.Id > 0)
                {
                    _indexerRepository.SetFields((IndexerDefinition)Definition, m => m.Settings);
                    _logger.Debug("Persisted refreshed Tidal tokens to indexer settings (expires {Expires:o})", user.ExpirationDate);
                }
            }
            catch (Exception ex)
            {
                // Failing to persist is non-fatal for THIS request (the new
                // tokens are already live in memory) but means the next
                // Lidarr restart will load the stale tokens. Log so the user
                // can spot drift if it happens repeatedly.
                _logger.Warn(ex, "Tidal token refresh succeeded in memory but could not be saved to indexer settings");
            }
        }

        // Lidarr's HttpIndexerBase catches HttpException and just logs it —
        // there is no built-in retry for 401-with-expired-token. Override
        // here so a search hitting the standard 24h Tidal access-token
        // expiry triggers a refresh (which auto-persists via the callback
        // wired in EnsureTokensLoaded) and one retry, instead of bubbling
        // the failure up to the user.
        protected override async Task<IList<ReleaseInfo>> FetchPage(IndexerRequest request, IParseIndexerResponse parser)
        {
            try
            {
                return await base.FetchPage(request, parser);
            }
            catch (HttpException ex) when (ShouldAttemptRefresh(ex))
            {
                _logger.Warn("Tidal search hit {Status}; attempting token refresh and retrying once", ex.Response.StatusCode);

                bool refreshed;
                try
                {
                    refreshed = await TidalAPI.Instance!.Client.ForceRefreshToken();
                }
                catch (Exception refreshEx)
                {
                    _logger.Error(refreshEx, "Tidal token refresh threw during search retry; user must re-authenticate");
                    throw;
                }

                if (!refreshed)
                {
                    _logger.Error("Tidal token refresh returned false during search retry; refresh_token is likely dead. User must re-authenticate.");
                    throw;
                }

                _logger.Debug("Tidal token refresh ok during search retry; re-stamping Authorization header and retrying");

                // Re-stamp the Authorization header with the refreshed token.
                // The original IndexerRequest captured the OLD bearer at
                // generation time, so a naked retry would 401 again.
                var user = TidalAPI.Instance!.Client.ActiveUser;
                if (user != null)
                {
                    request.HttpRequest.Headers.Remove("Authorization");
                    request.HttpRequest.Headers.Add("Authorization", $"{user.TokenType} {user.AccessToken}");
                }

                return await base.FetchPage(request, parser);
            }
        }

        private bool ShouldAttemptRefresh(HttpException ex)
        {
            if (ex.Response?.StatusCode != HttpStatusCode.Unauthorized)
                return false;

            if (string.IsNullOrEmpty(TidalAPI.Instance?.Client.ActiveUser?.RefreshToken))
                return false;

            return ExpiredTokenDetector.LooksExpired(
                ex.Response.Content,
                requestHadCountryCode: !string.IsNullOrEmpty(TidalAPI.Instance?.Client.ActiveUser?.CountryCode));
        }
    }
}
