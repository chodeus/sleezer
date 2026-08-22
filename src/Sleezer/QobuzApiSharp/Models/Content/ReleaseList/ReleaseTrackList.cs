// Vendored from DaveBinM/QobuzApiSharp (GPL-3.0), kept structurally as-is so
// upstream fixes can be pulled by hand. Nullable is off for the same reason.
#nullable disable
using Newtonsoft.Json;
using System.Collections.Generic;

namespace QobuzApiSharp.Models.Content
{
    public class ReleaseTrackList
    {
        [JsonProperty("has_more")]
        public bool HasMore { get; set; }

        [JsonProperty("tracks")]
        public List<Release> Items { get; set; }
    }
}