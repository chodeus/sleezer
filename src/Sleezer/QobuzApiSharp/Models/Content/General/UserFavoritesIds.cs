// Vendored from DaveBinM/QobuzApiSharp (GPL-3.0), kept structurally as-is so
// upstream fixes can be pulled by hand. Nullable is off for the same reason.
#nullable disable
using Newtonsoft.Json;
using System.Collections.Generic;

namespace QobuzApiSharp.Models.Content
{
    public class UserFavoritesIds
    {
        [JsonProperty("albums")]
        public List<string> Albums { get; set; }

        [JsonProperty("articles")]
        public List<long> Articles { get; set; }

        [JsonProperty("artists")]
        public List<int> Artists { get; set; }

        [JsonProperty("tracks")]
        public List<int> Tracks { get; set; }
    }
}