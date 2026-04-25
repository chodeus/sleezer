using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NzbDrone.Plugin.Sleezer.Core.Tidal
{
    // Pure-function path sanitizer extracted so the test project can exercise
    // it without dragging in TidalSharp / Lidarr.Core. Used by Tidal's
    // download client MetadataUtilities; behaviour traced to upstream
    // TrevTV/Lidarr.Plugin.Tidal issue #52.
    internal static class TidalPathSanitizer
    {
        // Filesystem-illegal everywhere we care about plus characters Linux
        // permits but Lidarr's path parser treats as separators or quotes.
        private static readonly char[] ExtraInvalidChars = { '\\', ':', '*', '?', '<', '>', '|', '/', '"' };

        public static string CleanPath(string str)
        {
            if (string.IsNullOrEmpty(str))
                return str;

            HashSet<char> banned = new(Path.GetInvalidFileNameChars());
            foreach (var c in ExtraInvalidChars)
                banned.Add(c);

            StringBuilder sb = new(str.Length);
            foreach (char c in str)
                sb.Append(banned.Contains(c) ? '_' : c);

            // Trim trailing whitespace and dots — Windows refuses these and
            // some FUSE mounts on Unraid behave the same way.
            return sb.ToString().TrimEnd('.', ' ');
        }
    }
}
