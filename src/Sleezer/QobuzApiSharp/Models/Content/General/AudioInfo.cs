// Vendored from DaveBinM/QobuzApiSharp (GPL-3.0), kept structurally as-is so
// upstream fixes can be pulled by hand. Nullable is off for the same reason.
#nullable disable
using Newtonsoft.Json;

namespace QobuzApiSharp.Models.Content
{
    public class AudioInfo
    {
        [JsonProperty("replaygain_track_gain")]
        public double? ReplaygainTrackGain { get; set; }

        [JsonProperty("replaygain_track_peak")]
        public double? ReplaygainTrackPeak { get; set; }
    }
}