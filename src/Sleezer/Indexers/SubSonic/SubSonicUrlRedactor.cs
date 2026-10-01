using System.Text.RegularExpressions;

namespace NzbDrone.Plugin.Sleezer.Indexers.SubSonic
{
    /// <summary>Removes SubSonic's login parameters from a URL before it is logged or put in an error.</summary>
    public static partial class SubSonicUrlRedactor
    {
        // Lidarr's log cleaner doesn't know Subsonic's one-letter u, p, t and s parameters.
        [GeneratedRegex(@"(?<=[?&])([upts])=[^&#\s]*", RegexOptions.IgnoreCase)]
        private static partial Regex LoginParameter();

        public static string Redact(string? url) =>
            url == null ? string.Empty : LoginParameter().Replace(url, "$1=(removed)");
    }
}
