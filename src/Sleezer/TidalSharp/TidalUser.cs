using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using TidalSharp.Data;
using TidalSharp.Exceptions;

namespace TidalSharp;

[JsonObject(MemberSerialization.OptIn)]
public class TidalUser
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

    [JsonConstructor]
    internal TidalUser(OAuthTokenData data, string? jsonPath, bool isPkce, DateTime? expirationDate)
    {
        _data = data;
        _jsonPath = jsonPath;
        ExpirationDate = expirationDate ?? DateTime.MinValue;
        IsPkce = isPkce;
    }

    internal async Task GetSession(API api, CancellationToken token = default)
    {
        JObject result = await api.Call(HttpMethod.Get, "sessions", token: token);

        try
        {
            _sessionInfo = result.ToObject<SessionInfo>();
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Failed to deserialize Tidal session info response");
            throw new APIException("Invalid response for session info.", ex);
        }
    }

    internal async Task RefreshOAuthTokenData(OAuthTokenData data, CancellationToken token = default)
    {
        if (_data == null)
            throw new InvalidOperationException("Attempting to refresh a user with no existing data.");

        _data.AccessToken = data.AccessToken;
        _data.ExpiresIn = data.ExpiresIn;

        // Tidal's refresh endpoint may rotate the refresh_token. When it
        // does, we MUST persist the new value — the old one is dead. When
        // the response omits refresh_token, keep the existing one (matches
        // Lidarr's Spotify import-list pattern).
        if (!string.IsNullOrEmpty(data.RefreshToken))
            _data.RefreshToken = data.RefreshToken;

        DateTime now = DateTime.UtcNow;
        ExpirationDate = now.AddSeconds(data.ExpiresIn);

        await WriteToFile(token);

        // Surface the refreshed tokens to whatever loaded this user, so the
        // caller (the Lidarr indexer) can persist them back to its settings
        // store. Without this, refreshes only live in memory and the saved
        // settings drift further out of date with each refresh — eventually
        // the saved refresh_token is the dead pre-rotation value and the
        // user has to re-authenticate manually.
        OnTokensRefreshed?.Invoke(this);
    }

    // Set by the loader (Core.LoadFromTokens) so refreshes have somewhere
    // to surface. Public-set so tests can inject; intentionally not an
    // event because there is exactly one persistence sink (the indexer
    // settings) and multi-cast semantics would just add subscription-leak
    // bugs around re-auth.
    public Action<TidalUser>? OnTokensRefreshed { get; set; }

    internal void UpdateJsonPath(string? jsonPath) => _jsonPath = jsonPath;

    internal async Task WriteToFile(CancellationToken token = default)
    {
        if (_jsonPath != null)
            await File.WriteAllTextAsync(_jsonPath, JsonConvert.SerializeObject(this), token);
    }

    private string? _jsonPath;

    [JsonProperty("Data")]
    private OAuthTokenData _data;
    private SessionInfo? _sessionInfo;

    [JsonProperty("ExpirationDate")]
    public DateTime ExpirationDate { get; private set; } = DateTime.MinValue;

    [JsonProperty("IsPkce")]
    public bool IsPkce { get; init; }

    public string AccessToken => _data.AccessToken;
    public string RefreshToken => _data.RefreshToken;
    public string TokenType => _data.TokenType;

    public long UserId => _data.UserId;
    // Fall back to the OAuth token's user.countryCode before the /sessions
    // bootstrap call has populated _sessionInfo. Tidal now rejects requests
    // (including the /sessions call itself) that arrive without countryCode,
    // so the very first request after a fresh login or token-restore needs a
    // value from somewhere other than the not-yet-fetched session info.
    public string CountryCode => _sessionInfo?.CountryCode ?? _data.User?.CountryCode ?? "";
    public string SessionID => _sessionInfo?.SessionId ?? "";
}
