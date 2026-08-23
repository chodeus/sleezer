// Vendored from DaveBinM/QobuzApiSharp (GPL-3.0), kept structurally as-is so
// upstream fixes can be pulled by hand. Nullable is off for the same reason.
#nullable disable
using Newtonsoft.Json;

namespace QobuzApiSharp.Models.Content
{
    public class ReleasePhysicalSupport
    {
        [JsonProperty("media_number")]
        public long? MediaNumber { get; set; }

        [JsonProperty("track_number")]
        public long? TrackNumber { get; set; }
    }
}