using System.Text.RegularExpressions;

namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    /// <summary>
    /// Drops store boilerplate from a title's version ("Album Version (Explicit)", "Remastered 2023"),
    /// which MusicBrainz titles never carry, and keeps versions that name a different recording.
    /// </summary>
    public static partial class StoreVersionFilter
    {
        [GeneratedRegex(@"^(?:explicit|clean|(?:explicit|clean) version|(?:(?:explicit|clean|original|lp) )?album version(?: explicit)?|(?:\d{4} )?(?:digital(?:ly)? )?remaster(?:ed)?(?: version)?(?: (?:in )?\d{4})?(?: version)?)$", RegexOptions.IgnoreCase)]
        private static partial Regex Boilerplate();

        [GeneratedRegex(@"\s*[/;]\s*|\s+-\s+|\s*[\(\)\[\]]\s*")]
        private static partial Regex Separators();

        // "Remastered (2023)" and "Remastered - 2023" are one segment, so the year goes with the wording.
        [GeneratedRegex(@"\b(remaster(?:ed)?)\s*(?:[\(\[]\s*(\d{4})\s*[\)\]]|-\s*(\d{4})\b)", RegexOptions.IgnoreCase)]
        private static partial Regex RemasterYear();

        // Returns the version unchanged when nothing in it is boilerplate, null when all of it is.
        public static string? Meaningful(string? version)
        {
            if (string.IsNullOrWhiteSpace(version))
                return null;

            string trimmed = version.Trim();
            string joined = RemasterYear().Replace(trimmed, "$1 $2$3");
            string[] segments = Separators().Split(joined).Where(s => s.Length > 0).ToArray();
            string[] kept = segments.Where(s => !Boilerplate().IsMatch(s)).ToArray();

            if (kept.Length == segments.Length)
                return trimmed;
            if (kept.Length == 0)
                return null;

            string rebuilt = string.Join(" / ", kept);
            return trimmed.StartsWith('(') && trimmed.EndsWith(')') ? $"({rebuilt})" : rebuilt;
        }

        /// <summary>A store title with its meaningful version; a title that already ends in the boilerplate version loses it.</summary>
        public static string TitleWithVersion(string title, string? version)
        {
            string? meaningful = Meaningful(version);
            if (!string.IsNullOrWhiteSpace(version) && meaningful != version.Trim() && title.EndsWith($"({version.Trim()})", StringComparison.Ordinal))
                title = title[..^(version.Trim().Length + 2)].TrimEnd();

            // Some store titles already carry their version (Tidal album 311544258).
            return !string.IsNullOrEmpty(meaningful) && !title.Contains(meaningful, StringComparison.InvariantCulture)
                ? $"{title} ({meaningful})"
                : title;
        }
    }
}
