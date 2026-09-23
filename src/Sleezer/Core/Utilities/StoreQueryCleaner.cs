using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NzbDrone.Common.Extensions;

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

        // Version words the stores keep in a `version` field their search does not index. Query
        // side only: StoreReleaseVerifier compares titles with StripQualifiers and must keep them.
        private static readonly Regex VersionSubtitle = new(
            @"\s*[:\-–—]\s[^:]*\b(?:re[-‐]?mix(?:es|ed)?|rework(?:s|ed)?|mix(?:es)?|edits?|version|live|acoustic|instrumentals?" +
            @"|remaster\w*|anniversary|deluxe|expanded|edition|reissue|demos?|mono|stereo)\b.*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TrailingVersion = new(
            @"\s*[:\-–—]?\s*\b(?:the\s+)?(?:re[-‐]?mix(?:es|ed)?|rework(?:s|ed)?)\b.*$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex TrailingLiveAt = new(@"\s+\blive\s+at\b.*$", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        // "S.H.I.E.L.D.": the stores match "SHIELD", not the "S H I E L D" punctuation stripping yields.
        private static readonly Regex DottedAcronym = new(@"(?<!\w)(?:\p{L}\.){2,}\p{L}?(?!\w)", RegexOptions.Compiled);

        // "Imagine: The Evolution Documentary" is listed as "Imagine".
        private static readonly Regex TrailingSubtitle = new(@"^(.*?\S)\s*:\s+\S.*$", RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex Apostrophes = new(@"['`´‘’]", RegexOptions.Compiled);
        private static readonly Regex NonAlphanumeric = new(@"[^\p{L}\p{M}\p{N}]+", RegexOptions.Compiled);

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

        /// <summary>StripQualifiers plus the remix/live/edition words a store search cannot see; keeps the original when nothing would remain.</summary>
        public static string StripForSearch(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return title;

            var stripped = VersionSubtitle.Replace(StripQualifiers(title), string.Empty);
            stripped = TrailingVersion.Replace(stripped, string.Empty);
            stripped = TrailingLiveAt.Replace(stripped, string.Empty);
            stripped = CollapseSpaces.Replace(stripped, " ").Trim(' ', ':', '-', '–', '—');

            return string.IsNullOrWhiteSpace(stripped) ? title : stripped;
        }

        /// <summary>"S.H.I.E.L.D." becomes "SHIELD"; token searches do not unify the two forms.</summary>
        public static string CollapseAcronyms(string value) =>
            string.IsNullOrEmpty(value) ? value : DottedAcronym.Replace(value, m => m.Value.Replace(".", string.Empty));

        /// <summary>Drops a trailing ": subtitle"; the stores keep those in the version field too.</summary>
        public static string StripTrailingSubtitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return title;

            var match = TrailingSubtitle.Match(title);
            return match.Success ? match.Groups[1].Value : title;
        }

        /// <summary>Normalised title, edition qualifier kept, so a remix or live edition never reads as the studio one; null when nothing remains.</summary>
        // Deliberately not StripForSearch: that is the query rule, and reusing it here would let a
        // lesser edition satisfy a search for a remix one.
        public static string? MatchKey(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return null;

            var key = StripLatinAccents(Apostrophes.Replace(title, string.Empty)).ToLowerInvariant();
            key = NonAlphanumeric.Replace(key, " ").Trim();

            return key.Length == 0 ? null : key;
        }

        // Not RemoveAccent, which drops every combining mark: on Japanese, Thai or Indic text the mark
        // changes the word, and two titles folding to one key would let the wrong album pass the gate.
        private static string StripLatinAccents(string text)
        {
            var decomposed = text.Normalize(NormalizationForm.FormD);
            var result = new StringBuilder(decomposed.Length);
            var onLatin = false;

            foreach (var c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                {
                    if (onLatin)
                        continue;
                }
                else
                {
                    onLatin = c < '\u0250' || c is >= '\u1E00' and <= '\u1EFF';
                }

                result.Append(c);
            }

            return result.ToString().Normalize(NormalizationForm.FormC);
        }

        /// <summary>Drops literal double quotes — Deezer's artist:"…" / album:"…" field syntax has no escape for them.</summary>
        public static string WithoutQuotes(string value) =>
            string.IsNullOrEmpty(value) ? value : CollapseSpaces.Replace(value.Replace('"', ' '), " ").Trim();

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
