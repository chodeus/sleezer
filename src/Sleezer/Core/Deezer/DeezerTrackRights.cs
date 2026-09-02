using Newtonsoft.Json.Linq;

namespace NzbDrone.Plugin.Sleezer.Core.Deezer
{
    /// <summary>Reads Deezer's per-entry RIGHTS block, which the gateway computes for the session's country.</summary>
    public static class DeezerTrackRights
    {
        /// <summary>True/false when RIGHTS is present, null when Deezer did not send it.</summary>
        public static bool? Streamable(JToken? entry)
        {
            var rights = entry?["RIGHTS"];
            if (rights == null || rights.Type == JTokenType.Null)
                return null;

            return rights["STREAM_SUB_AVAILABLE"]?.ToObject<bool?>() == true
                || rights["STREAM_ADS_AVAILABLE"]?.ToObject<bool?>() == true;
        }
    }
}
