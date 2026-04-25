using Newtonsoft.Json;

namespace TidalSharp.Data;

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
public class SessionInfo
{
    [JsonProperty("sessionId")]
    public string SessionId { get; set; }

    [JsonProperty("userId")]
    public long UserId { get; set; }

    [JsonProperty("countryCode")]
    public string CountryCode { get; set; }

    [JsonProperty("channelId")]
    public long ChannelId { get; set; }

    [JsonProperty("partnerId")]
    public long PartnerId { get; set; }

    [JsonProperty("client")]
    public ClientData Client { get; set; }

    public class ClientData
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("authorizedForOffline")]
        public bool AuthorizedForOffline { get; set; }
    }
}
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.