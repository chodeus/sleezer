using Newtonsoft.Json;

namespace TidalSharp.Data;

#pragma warning disable CS8618
public class DeviceAuthorizationResponse
{
    [JsonProperty("deviceCode")]
    public string DeviceCode { get; set; }

    [JsonProperty("userCode")]
    public string UserCode { get; set; }

    [JsonProperty("verificationUri")]
    public string VerificationUri { get; set; }

    [JsonProperty("verificationUriComplete")]
    public string VerificationUriComplete { get; set; }

    [JsonProperty("expiresIn")]
    public int ExpiresIn { get; set; }

    [JsonProperty("interval")]
    public int Interval { get; set; }
}
#pragma warning restore CS8618
