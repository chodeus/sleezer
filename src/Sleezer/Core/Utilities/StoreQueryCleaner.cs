using System.Text.RegularExpressions;

namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    /// <summary>Reduces a MusicBrainz title to what store searches actually carry; shared by the Deezer and Qobuz generators.</summary>
    public static class StoreQueryCleaner
    {
        private static readonly Regex BracketedGroups = new(@"\s*[\(\[][^\)\]]*[\)\]]", RegexOptions.Compiled);

        // A colon/dash subtitle mentioning soundtrack words is dropped wholesale — "The Hack:
        // Original Television Soundtrack" is just "The Hack" on the stores.
        private static readonly Regex SubtitleQualifier = new(
            @"\s*[:\-–—]\s[^:]*\b(?:sound\s?tracks?|score|OST|music\s+from)\b.*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TrailingQualifier = new(
            @"\s*[:\-–—]?\s*\b(?:" +
            @"(?:original\s+)?(?:motion\s+picture\s+)?(?:sound\s?tracks?|score)" +
            @"|OST" +
            @"|(?:\d+(?:st|nd|rd|th)?\s+)?anniversary\s+edition" +
            @"|special\s+edition" +
            @"|deluxe|expanded|remaster\w*|bonus\s+track\w*|EP|single" +
            @")\b.*$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex CollapseSpaces = new(@"\s{2,}", RegexOptions.Compiled);

        /// <summary>Strips bracketed groups and edition/soundtrack qualifiers; keeps the original when nothing would remain.</summary>
        public static string StripQualifiers(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return title;

            var stripped = BracketedGroups.Replace(title, string.Empty);
            stripped = SubtitleQualifier.Replace(stripped, string.Empty);
            stripped = TrailingQualifier.Replace(stripped, string.Empty);
            stripped = CollapseSpaces.Replace(stripped, " ").Trim(' ', ':', '-', '–', '—');

            return string.IsNullOrWhiteSpace(stripped) ? title : stripped;
        }

        /// <summary>'+' and apostrophes become spaces — token-AND searches don't unify them.</summary>
        public static string CleanForTokenSearch(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return query;

            var cleaned = query.Replace('+', ' ').Replace('\'', ' ');
            return CollapseSpaces.Replace(cleaned, " ").Trim();
        }
    }
}
