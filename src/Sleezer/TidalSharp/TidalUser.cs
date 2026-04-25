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

        DateTime now = DateTime.UtcNow;
        ExpirationDate = now.AddSeconds(data.ExpiresIn);

        await WriteToFile(token);
    }

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
