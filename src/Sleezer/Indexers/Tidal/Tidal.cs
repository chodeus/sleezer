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
                    return StartOAuth();
                case "getOAuthToken":
                    return GetOAuthToken();
                default:
                    return base.RequestAction(action, query);
            }
        }

        private object StartOAuth()
        {
            EnsureApiInitialized();
            try
            {
                var auth = TidalAPI.Instance!.Client.BeginDeviceLogin().GetAwaiter().GetResult();
                lock (_authLock)
                {
                    _pendingDeviceAuth = auth;
                }

                string url = auth.VerificationUriComplete;
                if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    url = "https://" + url;

                _logger.Info("Tidal device authorization started; user code {UserCode}, expires in {Expires}s", auth.UserCode, auth.ExpiresIn);
                return new
                {
                    OauthUrl = url,
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
