// Vendored from DaveBinM/QobuzApiSharp (GPL-3.0), kept structurally as-is so
// upstream fixes can be pulled by hand. Nullable is off for the same reason.
#nullable disable
using Newtonsoft.Json;

namespace QobuzApiSharp.Models.User
{
    public class LastUpdate
    {
        [JsonProperty("favorite")]
        public long? Favorite { get; set; }

        [JsonProperty("playlist")]
        public long? Playlist { get; set; }

        [JsonProperty("purchase")]
        public long? Purchase { get; set; }
    }
}