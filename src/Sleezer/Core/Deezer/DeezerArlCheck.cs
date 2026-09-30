using Newtonsoft.Json.Linq;

namespace NzbDrone.Plugin.Sleezer.Core.Deezer
{
    public static class DeezerArlCheck
    {
        // SetARL always yields a session; a rejected ARL is an anonymous one, USER_ID 0.
        public static bool HasSignedInUser(JToken? userData) =>
            (userData?["USER"]?["USER_ID"]?.Value<long?>() ?? 0) != 0;

        // A free plan signs in fine, then every download above 128 kbps fails on license rights.
        public static string? StreamingProblem(JToken? userData)
        {
            if (!HasSignedInUser(userData))
                return null;

            JToken? options = userData!["USER"]?["OPTIONS"];
            bool Flag(string name) => options?[name]?.Type == JTokenType.Boolean && options[name]!.Value<bool>();

            if (Flag("web_lossless") || Flag("mobile_lossless") || Flag("web_hq") || Flag("mobile_hq"))
                return null;

            string userId = userData["USER"]?["USER_ID"]?.ToString() ?? "?";
            return $"Deezer account {userId} has no HQ or lossless streaming (free plan). Downloads at 320 kbps or FLAC will fail until the account has a paid plan.";
        }
    }
}
