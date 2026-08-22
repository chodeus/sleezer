// Vendored from DaveBinM/QobuzApiSharp (GPL-3.0), kept structurally as-is so
// upstream fixes can be pulled by hand. Nullable is off for the same reason.
#nullable disable
using Newtonsoft.Json;
using System;

namespace QobuzApiSharp.Models.Content
{
    public class ReleaseDates
    {
        [JsonProperty("download")]
        public DateTimeOffset? Download { get; set; }

        [JsonProperty("original")]
        public DateTimeOffset? Original { get; set; }

        [JsonProperty("stream")]
        public DateTimeOffset? Stream { get; set; }
    }
}