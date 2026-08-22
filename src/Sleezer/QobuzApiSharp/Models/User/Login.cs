// Vendored from DaveBinM/QobuzApiSharp (GPL-3.0), kept structurally as-is so
// upstream fixes can be pulled by hand. Nullable is off for the same reason.
#nullable disable
using Newtonsoft.Json;

namespace QobuzApiSharp.Models.User
{
    public class Login
    {
        [JsonProperty("user")]
        public User User { get; set; }

        [JsonProperty("user_auth_token")]
        public string AuthToken { get; set; }
    }
}