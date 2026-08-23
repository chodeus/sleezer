// Vendored from DaveBinM/QobuzApiSharp (GPL-3.0), kept structurally as-is so
// upstream fixes can be pulled by hand. Nullable is off for the same reason.
#nullable disable
using Newtonsoft.Json;

namespace QobuzApiSharp.Models.Content
{
    public class ArticleIds
    {
        [JsonProperty("LLS")]
        public int? LLS { get; set; }

        [JsonProperty("SHP")]
        public int? SHP { get; set; }

        [JsonProperty("SMR")]
        public int? SMR { get; set; }
    }
}