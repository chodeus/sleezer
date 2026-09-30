using System.Text.RegularExpressions;

namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    /// <summary>
    /// Drops store boilerplate from a title's version ("Album Version (Explicit)", "Remastered 2023"),
    /// which MusicBrainz titles never carry, and keeps versions that name a different recording.
    /// </summary>
    public static partial class StoreVersionFilter
    {
        [GeneratedRegex(@"^(?:explicit|clean|(?:explicit|clean) version|(?:(?:explicit|original|lp) )?album version(?: explicit)?|(?:\d{4} )?(?:digital(?:ly)? )?remaster(?:ed)?(?: version)?(?: (?:in )?\d{4})?(?: version)?)$", RegexOptions.IgnoreCase)]
        private static partial Regex Boilerplate();

        [GeneratedRegex(@"\s*[/;]\s*|\s+-\s+|\s*[\(\)\[\]]\s*")]
        private static partial Regex Separators();

        // Returns the version unchanged when nothing in it is boilerplate, null when all of it is.
        public static string? Meaningful(string? version)
        {
            if (string.IsNullOrWhiteSpace(version))
                return null;

            string trimmed = version.Trim();
            string[] segments = Separators().Split(trimmed).Where(s => s.Length > 0).ToArray();
            string[] kept = segments.Where(s => !Boilerplate().IsMatch(s)).ToArray();

            if (kept.Length == segments.Length)
                return trimmed;
            if (kept.Length == 0)
                return null;

            string rebuilt = string.Join(" / ", kept);
            return trimmed.StartsWith('(') && trimmed.EndsWith(')') ? $"({rebuilt})" : rebuilt;
        }
    }
}
