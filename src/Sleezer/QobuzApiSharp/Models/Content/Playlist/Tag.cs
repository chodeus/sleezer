// Vendored from DaveBinM/QobuzApiSharp (GPL-3.0), kept structurally as-is so
// upstream fixes can be pulled by hand. Nullable is off for the same reason.
#nullable disable
using Newtonsoft.Json;

namespace QobuzApiSharp.Models.Content
{
    public class Tag
    {
        [JsonProperty("featured_tag_id")]
        public string FeaturedTagId { get; set; }

        [JsonProperty("name_json")]
        public string NameJson { get; set; }

        [JsonProperty("slug")]
        public string Slug { get; set; }

        [JsonProperty("color")]
        public string Color { get; set; }

        [JsonProperty("genre_tag")]
        public GenreTag GenreTag { get; set; }

        [JsonProperty("is_discover")]
        public bool? IsDiscover { get; set; }
    }
}
