using Newtonsoft.Json;
using NLog;
using NzbDrone.Common.Http;
using TidalSharp.Data;

namespace TidalSharp;

public class TidalClient
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

    public TidalClient(string? dataDir, IHttpClient httpClient)
    {
        _dataPath = dataDir;
        _userJsonPath = _dataPath == null ? null : Path.Combine(_dataPath, "lastUser.json");

        if (_dataPath != null && !Directory.Exists(_dataPath))
            Directory.CreateDirectory(_dataPath);

        _httpClient = httpClient;

        _session = new(_httpClient);
        API = new(_httpClient, _session);
        Downloader = new(_httpClient, API, _session);
    }

    public API API { get; init; }
    public Downloader Downloader { get; init; }
    public TidalUser? ActiveUser { get; set; }

    private Session _session;

    private string? _dataPath;
    private string? _userJsonPath;
    private string? _lastRedirectUri;

    private IHttpClient _httpClient;

    public async Task<bool> Login(string? redirectUri = null, CancellationToken token = default)
    {
        var shouldCheckFile = _lastRedirectUri == null || _lastRedirectUri == redirectUri; // prevents us from loading the old user when the redirect uri is updated
        if (shouldCheckFile && await CheckForStoredUser(token))
        {
            _lastRedirectUri = redirectUri;
            return true;
        }

        if (string.IsNullOrEmpty(redirectUri))
            return false;

        var data = await _session.GetOAuthDataFromRedirect(redirectUri, token);
        if (data == null) return false;

        var user = new TidalUser(data, _userJsonPath, true, DateTime.UtcNow.AddSeconds(data.ExpiresIn));

        ActiveUser = user;
        API.UpdateUser(user);

        await user.GetSession(API, token);
        await user.WriteToFile(token);

        _lastRedirectUri = redirectUri;

        return true;
    }

    public async Task<bool> ForceRefreshToken(CancellationToken token = default)
    {
        if (ActiveUser == null)
            return false;

        return await _session.AttemptTokenRefresh(ActiveUser, token);
    }

    public async Task<bool> IsLoggedIn(CancellationToken token = default)
    {
        if (ActiveUser == null || string.IsNullOrEmpty(ActiveUser.SessionID))
            return false;

        try
        {
            var res = await API.Call(HttpMethod.Get, $"users/{ActiveUser.UserId}/subscription", token: token);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Tidal session probe failed; treating user as logged out");
            return false;
        }
    }

    public string GetPkceLoginUrl() => _session.GetPkceLoginUrl();

    public void RegeneratePkceCodes() => _session.RegenerateCodes();

    public Task<DeviceAuthorizationResponse> BeginDeviceLogin(CancellationToken token = default)
        => _session.StartDeviceAuthorization(token);

    public async Task<OAuthTokenData> CompleteDeviceLogin(DeviceAuthorizationResponse auth, CancellationToken token = default)
    {
        OAuthTokenData data = await _session.PollForDeviceToken(auth, token);

        var user = new TidalUser(data, _userJsonPath, isPkce: false, DateTime.UtcNow.AddSeconds(data.ExpiresIn));

        ActiveUser = user;
        API.UpdateUser(user);

        await user.GetSession(API, token);
        await user.WriteToFile(token);

        return data;
    }

    // Restore an active session from previously persisted token data — typical
    // path when Lidarr loads our plugin and hands us tokens out of its own
    // settings store. Skips file IO entirely; pairs with IPluginSettings-backed
    // token storage instead of the legacy lastUser.json flow.
    public async Task LoadFromTokens(string accessToken, string refreshToken, string tokenType, long userId, DateTime expirationDate, string countryCode = "", Action<TidalUser>? onTokensRefreshed = null, CancellationToken token = default)
    {
        long secondsRemaining = (long)Math.Max(0, (expirationDate - DateTime.UtcNow).TotalSeconds);
        var data = new OAuthTokenData
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            TokenType = string.IsNullOrEmpty(tokenType) ? "Bearer" : tokenType,
            UserId = userId,
            ExpiresIn = secondsRemaining,
            Scope = "r_usr+w_usr+w_sub",
            ClientName = "device",
            User = new OAuthTokenData.UserData { UserId = userId, CountryCode = countryCode ?? "" }
        };

        var user = new TidalUser(data, jsonPath: null, isPkce: false, expirationDate)
        {
            OnTokensRefreshed = onTokensRefreshed
        };

        ActiveUser = user;
        API.UpdateUser(user);

        // Refresh first if expired; otherwise just pull SessionID/CountryCode.
        // The OnTokensRefreshed hook fires from inside AttemptTokenRefresh on
        // success, so even this initial load-time refresh gets persisted.
        if (expirationDate <= DateTime.UtcNow)
            await _session.AttemptTokenRefresh(user, token);

        await user.GetSession(API, token);
    }

    private async Task<bool> CheckForStoredUser(CancellationToken token = default)
    {
        if (_userJsonPath != null && File.Exists(_userJsonPath))
        {
            try
            {
                var userData = await File.ReadAllTextAsync(_userJsonPath, token);
                var user = JsonConvert.DeserializeObject<TidalUser>(userData);
                if (user == null) return false;

                user.UpdateJsonPath(_userJsonPath);

                ActiveUser = user;
                API.UpdateUser(user);

                await user.GetSession(API, token);

                return true;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to restore Tidal user from {Path}", _userJsonPath);
                return false;
            }
        }

        return false;
    }
}
