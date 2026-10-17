using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    /// <summary>One comparable form of a name or title, shared by every source; each caller layers its own rules on top.</summary>
    public static partial class ComparableText
    {
        // FormD decomposition handles combining accents (é → e) but not these
        // standalone codepoints, which appear verbatim in MusicBrainz names.
        private static readonly Dictionary<char, string> LigatureMap = new()
        {
            { 'æ', "ae" }, { 'Æ', "Ae" }, { 'œ', "oe" }, { 'Œ', "Oe" },
            { 'ø', "o" }, { 'Ø', "O" }, { 'ð', "d" }, { 'Ð', "D" },
            { 'þ', "th" }, { 'Þ', "Th" }, { 'ß', "ss" }, { 'đ', "d" },
            { 'Đ', "D" }, { 'ł', "l" }, { 'Ł', "L" }, { 'ı', "i" },
            { 'ẞ', "SS" }, { 'ς', "σ" },
        };

        /// <summary>
        /// Lower case with optional marks folded and typographic punctuation mapped; "&amp;" reads "and", "$" next to a
        /// letter reads "s", apostrophes and an initialism's dots or underscores join a word ("Beto’s" → "betos",
        /// "A.D.I.D.A.S." → "adidas"), and every other punctuation mark separates words.
        /// </summary>
        public static string Of(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            string folded = FoldOptionalMarks(MapPunctuation(text)).ToLowerInvariant();
            folded = DollarRegex().Replace(folded.Replace("&", " and "), "s");
            folded = SeparatorRegex().Replace(JoinWords(folded), " ");
            return WhitespaceRegex().Replace(folded, " ").Trim();
        }

        /// <summary>Apostrophes, and an initialism's dots or underscores, removed so "Beto’s" and "S.T.A.R." each stay one word; case is kept.</summary>
        public static string JoinWords(string text) => JoiningMarkRegex().Replace(MapPunctuation(text), string.Empty);

        /// <summary>Typographic dashes, quotes and ellipses as their ASCII forms.</summary>
        public static string MapPunctuation(string text)
        {
            StringBuilder sb = new(text.Length);
            foreach (char c in text)
            {
                sb.Append(c switch
                {
                    '‐' or '‑' or '‒' or '–' or '—' or '―' or '−' => "-",
                    '‘' or '’' or '‚' or 'ʼ' or '`' or '´' => "'",
                    '“' or '”' or '„' => "\"",
                    '…' => " ",
                    _ => c.ToString(),
                });
            }

            return sb.ToString();
        }

        /// <summary>The text without diacritics on any script, ligatures spelled out; case is kept. For search queries, not comparisons.</summary>
        public static string FoldAccents(string text) => Fold(text, keepSpellingMarks: false);

        /// <summary>Accents, tonos, harakat and niqqud folded, ligatures spelled out; marks that spell the word stay. Case is kept.</summary>
        public static string FoldOptionalMarks(string text) => Fold(text, keepSpellingMarks: true);

        private static string Fold(string text, bool keepSpellingMarks)
        {
            if (text.All(c => c <= 127))
                return text;

            string decomposed = text.Normalize(NormalizationForm.FormD);
            StringBuilder sb = new(decomposed.Length);
            char letter = '\0';
            foreach (char c in decomposed)
            {
                if (!IsMark(c))
                {
                    letter = c;
                    AppendSpelledOut(sb, c);
                }
                else if (keepSpellingMarks && MarkSpellsWord(letter, c))
                {
                    sb.Append(c);
                }
            }

            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        private static bool IsMark(char c) =>
            CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

        private static void AppendSpelledOut(StringBuilder sb, char c)
        {
            if (LigatureMap.TryGetValue(c, out string? replacement))
                sb.Append(replacement);
            else
                sb.Append(c);
        }

        // Thaana, Indic, Thai, Lao, Tibetan, Myanmar, Khmer, Tai Tham, Balinese, Sundanese, Javanese and kana: there the
        // mark changes the word ("ガラス" is not "カラス"). Cyrillic й, ї and ў are letters of their own; only ё folds.
        private static bool MarkSpellsWord(char letter, char mark) =>
            letter is (>= '\u0780' and <= '\u07BF') or (>= '\u0900' and <= '\u109F') or (>= '\u1780' and <= '\u17FF')
                or (>= '\u1A20' and <= '\u1BBF') or (>= '\u3040' and <= '\u30FF') or (>= '\uA980' and <= '\uA9DF')
            || (letter is >= '\u0400' and <= '\u04FF' && (mark == '\u0306' || (mark == '\u0308' && letter is not ('е' or 'Е'))));

        // An apostrophe anywhere, or a dot or underscore between two single letters ("S_W_A_T"; "Song.FLAC" still splits).
        [GeneratedRegex(@"'|(?<=(?:^|[^\p{L}])\p{L})[._](?=\p{L}(?:[^\p{L}]|$))")]
        private static partial Regex JoiningMarkRegex();

        // \p{M}: a mark kept on a letter is part of the word, not a separator.
        [GeneratedRegex(@"[^\p{L}\p{M}\p{N}\s]")]
        private static partial Regex SeparatorRegex();

        // "Ke$ha", "A$AP": a dollar sign beside a letter; "$20" keeps its number.
        [GeneratedRegex(@"\$(?=\p{L})|(?<=\p{L})\$")]
        private static partial Regex DollarRegex();

        [GeneratedRegex(@"\s+")]
        private static partial Regex WhitespaceRegex();
    }
}
