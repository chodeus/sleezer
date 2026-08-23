// Vendored from DaveBinM/QobuzApiSharp (GPL-3.0), kept structurally as-is so
// upstream fixes can be pulled by hand. Nullable is off for the same reason.
#nullable disable
using Newtonsoft.Json;

namespace QobuzApiSharp.Models.Content
{
    public class UserFavorites
    {
        [JsonProperty("user")]
        public User.User User { get; set; }

        [JsonProperty("albums")]
        public ItemSearchResult<Album> Albums { get; set; }

        [JsonProperty("artists")]
        public ItemSearchResult<Artist> Artists { get; set; }

        [JsonProperty("tracks")]
        public ItemSearchResult<Track> Tracks { get; set; }

        [JsonProperty("articles")]
        public ItemSearchResult<Article> Articles { get; set; }
    }
}