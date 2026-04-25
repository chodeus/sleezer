using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using TidalSharp.Data;
using TidalSharp.Exceptions;

namespace TidalSharp;

internal class Session
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value; RegenerateCodes sets them, no idea why it's complaining
    internal Session(IHttpClient client, int itemLimit = 1000, bool alac = true)
#pragma warning restore CS8618
    {
        _httpClient = client;
        Alac = alac;

        ItemLimit = itemLimit > 10000 ? 10000 : itemLimit;

        RegenerateCodes();
    }

    public int ItemLimit { get; init; }
    public bool Alac { get; init; }

    private IHttpClient _httpClient;

    private string _clientUniqueKey;
    private string _codeVerifier;
    private string _codeChallenge;

    public string GetPkceLoginUrl()
    {
        var parameters = new Dictionary<string, string>
        {
            { "response_type", "code" },
            { "redirect_uri", Globals.PKCE_URI_REDIRECT },
            { "client_id", Globals.CLIENT_ID_PKCE },
            { "lang", "EN" },
            { "appMode", "android" },
            { "client_unique_key", _clientUniqueKey },
            { "code_challenge", _codeChallenge },
            { "code_challenge_method", "S256" },
            { "restrict_signup", "true" }
        };

        var queryString = HttpUtility.ParseQueryString(string.Empty);
        foreach (var param in parameters)
        {
            queryString[param.Key] = param.Value;
        }

        return $"{Globals.API_PKCE_AUTH}?{queryString}";
    }

    public async Task<bool> AttemptTokenRefresh(TidalUser user, CancellationToken token = default)
    {
        var request = _httpClient.BuildRequest(Globals.API_OAUTH2_TOKEN)
                        .Post()
                        .AddFormParameter("grant_type", "refresh_token")
                        .AddFormParameter("refresh_token", user.RefreshToken)
                        .AddFormParameter("client_id", user.IsPkce ? Globals.CLIENT_ID_PKCE : Globals.CLIENT_ID)
                        .AddFormParameter("client_secret", user.IsPkce ? Globals.CLIENT_SECRET_PKCE : Globals.CLIENT_SECRET);

        var response = await _httpClient.ProcessRequestAsync(request);

        if (response.HasHttpError)
            return false;

        try
        {
            var responseStr = response.Content;
            var tokenData = JObject.Parse(responseStr).ToObject<OAuthTokenData>()!;
            await user.RefreshOAuthTokenData(tokenData, token);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Tidal token refresh failed");
            return false;
        }
    }

    public async Task<OAuthTokenData?> GetOAuthDataFromRedirect(string? uri, CancellationToken token = default)
    {
        if (string.IsNullOrEmpty(uri) || !uri.StartsWith("https://"))
            throw new InvalidURLException("The provided redirect URL looks wrong: " + uri);

        var queryParams = HttpUtility.ParseQueryString(new Uri(uri).Query);
        string? code = queryParams.Get("code");
        if (string.IsNullOrEmpty(code))
            throw new InvalidURLException("Authorization code not found in the redirect URL.");

        var request = _httpClient.BuildRequest(Globals.API_OAUTH2_TOKEN)
                        .Post()
                        .AddFormParameter("code", code)
                        .AddFormParameter("client_id", Globals.CLIENT_ID_PKCE)
                        .AddFormParameter("grant_type", "authorization_code")
                        .AddFormParameter("redirect_uri", Globals.PKCE_URI_REDIRECT)
                        .AddFormParameter("scope", "r_usr+w_usr+w_sub")
                        .AddFormParameter("code_verifier", _codeVerifier)
                        .AddFormParameter("client_unique_key", _clientUniqueKey);

        var response = await _httpClient.ProcessRequestAsync(request);

        if (response.HasHttpError)
            throw new APIException($"Login failed: {Redact(response.Content)}");

        try
        {
            return JObject.Parse(response.Content).ToObject<OAuthTokenData>();
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Failed to deserialize Tidal OAuth token response");
            throw new APIException("Invalid response for the authorization code.", ex);
        }
    }

    public void RegenerateCodes()
    {
        _clientUniqueKey = $"{BitConverter.ToUInt64(Guid.NewGuid().ToByteArray(), 0):x}";
        _codeVerifier = ToBase64UrlEncoded(RandomNumberGenerator.GetBytes(32));

        using var sha256 = SHA256.Create();
        _codeChallenge = ToBase64UrlEncoded(sha256.ComputeHash(Encoding.UTF8.GetBytes(_codeVerifier)));
    }

    public async Task<DeviceAuthorizationResponse> StartDeviceAuthorization(CancellationToken token = default)
    {
        var request = _httpClient.BuildRequest(Globals.API_OAUTH2_DEVICE_AUTH)
                        .Post()
                        .AddFormParameter("client_id", Globals.CLIENT_ID_DEVICE)
                        .AddFormParameter("scope", "r_usr+w_usr+w_sub");

        var response = await _httpClient.ProcessRequestAsync(request);

        if (response.HasHttpError)
            throw new APIException($"Device authorization request failed ({(int)response.StatusCode}): {Redact(response.Content)}");

        try
        {
            return JObject.Parse(response.Content).ToObject<DeviceAuthorizationResponse>()!;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Failed to deserialize Tidal device authorization response");
            throw new APIException("Invalid response from Tidal device_authorization endpoint.", ex);
        }
    }

    // Polls Tidal's token endpoint until the user completes authorization in
    // the browser. Returns OAuthTokenData on success; throws APIException on
    // expired_token or access_denied. Honors the server-supplied poll interval
    // and respects slow_down responses by widening the interval by 5s.
    public async Task<OAuthTokenData> PollForDeviceToken(DeviceAuthorizationResponse auth, CancellationToken token = default)
    {
        int interval = Math.Max(1, auth.Interval);
        DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(60, auth.ExpiresIn));

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), token);

            var request = _httpClient.BuildRequest(Globals.API_OAUTH2_TOKEN)
                            .Post()
                            .AddFormParameter("client_id", Globals.CLIENT_ID_DEVICE)
                            .AddFormParameter("client_secret", Globals.CLIENT_SECRET_DEVICE)
                            .AddFormParameter("device_code", auth.DeviceCode)
                            .AddFormParameter("grant_type", "urn:ietf:params:oauth:grant-type:device_code")
                            .AddFormParameter("scope", "r_usr+w_usr+w_sub");

            var response = await _httpClient.ProcessRequestAsync(request);
            JObject body;
            try
            {
                body = JObject.Parse(response.Content);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Tidal device-token poll returned non-JSON body: {Body}", Redact(response.Content));
                throw new APIException("Invalid response from Tidal token endpoint during device polling.", ex);
            }

            if (!response.HasHttpError)
            {
                try
                {
                    return body.ToObject<OAuthTokenData>()!;
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Failed to deserialize Tidal device-token success response");
                    throw new APIException("Invalid OAuth token response from Tidal during device polling.", ex);
                }
            }

            string? error = body["error"]?.ToString();
            switch (error)
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += 5;
                    continue;
                case "expired_token":
                    throw new APIException("Tidal device authorization expired before the user completed login.");
                case "access_denied":
                    throw new APIException("User denied Tidal authorization.");
                default:
                    _logger.Debug("Tidal device-token poll returned unknown error: {Body}", Redact(response.Content));
                    throw new APIException($"Tidal device authorization failed: {error ?? Redact(response.Content)}");
            }
        }

        throw new APIException("Timed out waiting for Tidal device authorization to complete.");
    }

    private static string ToBase64UrlEncoded(byte[] data) => Convert.ToBase64String(data).Replace("+", "-").Replace("/", "_").TrimEnd('=');

    // Redact any well-known sensitive Tidal field before logging an HTTP
    // response body. Defensive: in normal operation we only log bodies on
    // error/unparseable paths, but a buggy proxy or transient mid-success
    // serialization could expose tokens.
    private static readonly Regex _sensitiveJsonField = new(
        "\"(access_token|refresh_token|device_code|user_code|client_secret)\"\\s*:\\s*\"[^\"]*\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static string Redact(string body)
        => string.IsNullOrEmpty(body)
            ? body
            : _sensitiveJsonField.Replace(body, m => $"\"{m.Groups[1].Value}\":\"REDACTED\"");
}
