// Vendored from DaveBinM/QobuzApiSharp (GPL-3.0), kept structurally as-is so
// upstream fixes can be pulled by hand. Nullable is off for the same reason.
#nullable disable
using Newtonsoft.Json;

namespace QobuzApiSharp.Models.Content
{
    public class ReleaseAudioInfo
    {
        [JsonProperty("maximum_bit_depth")]
        public int? MaximumBitDepth { get; set; }

        [JsonProperty("maximum_channel_count")]
        public int? MaximumChannelCount { get; set; }

        [JsonProperty("maximum_sampling_rate")]
        public double? MaximumSamplingRate { get; set; }
    }
}