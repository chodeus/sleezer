using System.Net;

namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    /// <summary>Whether a store's HTTP status can change on another try: a 429 or any 5xx.</summary>
    public static class TransientStatus
    {
        public static bool IsTransient(HttpStatusCode status) => IsTransient((int)status);

        public static bool IsTransient(int status) => status == 429 || status >= 500;
    }
}
