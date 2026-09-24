using Newtonsoft.Json.Linq;

namespace NzbDrone.Plugin.Sleezer.Core.Deezer
{
    public static class DeezerArlCheck
    {
        // SetARL always yields a session; a rejected ARL is an anonymous one, USER_ID 0.
        public static bool HasSignedInUser(JToken? userData) =>
            (userData?["USER"]?["USER_ID"]?.Value<long?>() ?? 0) != 0;
    }
}
