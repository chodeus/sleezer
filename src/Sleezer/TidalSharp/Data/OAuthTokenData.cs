using Newtonsoft.Json;

namespace TidalSharp.Data;

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
public class OAuthTokenData
{
    [JsonProperty("scope")]
    public string Scope { get; set; }

    [JsonProperty("user")]
    public UserData User { get; set; }

    [JsonProperty("clientName")]
    public string ClientName { get; set; }

    [JsonProperty("token_type")]
    public string TokenType { get; set; }

    [JsonProperty("access_token")]
    public string AccessToken { get; set; }

    [JsonProperty("refresh_token")]
    public string RefreshToken { get; set; }

    [JsonProperty("expires_in")]
    public long ExpiresIn { get; set; }

    [JsonProperty("user_id")]
    public long UserId { get; set; }

    public class UserData
    {
        [JsonProperty("userId")]
        public long UserId { get; set; }

        [JsonProperty("countryCode")]
        public string CountryCode { get; set; }
    }
}
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.