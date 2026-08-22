// Vendored from DaveBinM/QobuzApiSharp (GPL-3.0), kept structurally as-is so
// upstream fixes can be pulled by hand. Nullable is off for the same reason.
#nullable disable
using Newtonsoft.Json;

namespace QobuzApiSharp.Models.User
{
    public class Credential
    {
        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("id")]
        public long? Id { get; set; }

        [JsonProperty("label")]
        public string Label { get; set; }

        [JsonProperty("parameters")]
        public Parameters Parameters { get; set; }
    }
}