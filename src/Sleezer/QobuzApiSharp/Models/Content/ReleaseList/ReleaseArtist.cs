// Vendored from DaveBinM/QobuzApiSharp (GPL-3.0), kept structurally as-is so
// upstream fixes can be pulled by hand. Nullable is off for the same reason.
#nullable disable
using Newtonsoft.Json;

namespace QobuzApiSharp.Models.Content
{
    public class ReleaseArtist
    {
        [JsonProperty("name")]
        public ReleaseArtistName Name { get; set; }
    }

    public class ReleaseArtistName
    {
        [JsonProperty("display")]
        public string Display { get; set; }
    }
}