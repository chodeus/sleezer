using System.Text;
using System.Text.RegularExpressions;

namespace NzbDrone.Plugin.Sleezer.Indexers.Soulseek
{
    /// <summary>Whether a wanted track title stands alone in a file name: bounded by separators, numbers or its own qualifier.</summary>
    internal static partial class SlskdTitleMatch
    {
        private const string Separator = "|";

        /// <summary>The file title's words, with a separator token wherever a separator stood and at both ends.</summary>
        internal static List<string> Tokens(string fileTitle)
        {
            // A macOS peer's decomposed accents compose to match the title (FormKC would turn "´" and "…" into separators).
            string text = fileTitle.Normalize(NormalizationForm.FormC);
            // Underscores stand for spaces only in a name that has none; otherwise they separate fields.
            text = text.Contains(' ') ? text.Replace('_', '-') : text.Replace('_', ' ');
            List<string> tokens = [Separator];
            foreach (string piece in SeparatorRegex().Split(text))
            {
                string[] words = Words(piece, keepStopWords: true);
                if (words.Length == 0)
                    continue;

                tokens.AddRange(words);
                tokens.Add(Separator);
            }

            return tokens;
        }

        /// <summary>A wanted title's words, split at its first bracket into the head and the qualifier.</summary>
        internal static (string[] Head, string[] Qualifier) TitleWords(string wantedTitle)
        {
            string title = wantedTitle.Normalize(NormalizationForm.FormC);
            bool keepStopWords = OnlyStopWords(title);
            int bracket = title.IndexOfAny(['(', '[']);
            string[] head = Words(bracket > 0 ? title[..bracket] : title, keepStopWords);
            string[] qualifier = bracket > 0 ? Words(title[bracket..], keepStopWords) : [];
            return head.Length == 0 ? (qualifier, []) : (head, qualifier);
        }

        /// <summary>
        /// The fewest words after the title where it stands alone, or -1 when it never does: its head follows a separator
        /// or number and is followed by its qualifier words or, for a title without one, by a separator, a number or the end.
        /// </summary>
        internal static int WordsAfter(IReadOnlyList<string> tokens, (string[] Head, string[] Qualifier) title)
        {
            if (title.Head.Length == 0)
                return -1;

            IReadOnlyList<string> seen = SeenBy(tokens, title.Head);
            int fewest = -1;
            for (int i = 1; i < seen.Count && fewest != 0; i++)
            {
                if (seen[i] == Separator || !IsEdge(seen[i - 1]))
                    continue;

                int end = TitleEnd(seen, i, title);
                if (end < 0)
                    continue;

                int after = seen.Skip(end).Count(t => t != Separator);
                if (fewest < 0 || after < fewest)
                    fewest = after;
            }

            return fewest;
        }

        // A title made only of stop words ("With") is matched against them; every other title never sees them.
        private static IReadOnlyList<string> SeenBy(IReadOnlyList<string> tokens, string[] head) =>
            head.All(SlskdItemsParser.IsStopWord) ? tokens : [.. tokens.Where(t => !SlskdItemsParser.IsStopWord(t))];

        // Index just past the title read from `start`: its head, then its qualifier or, for a title without one, an edge or the end; -1 otherwise.
        private static int TitleEnd(IReadOnlyList<string> tokens, int start, (string[] Head, string[] Qualifier) title)
        {
            int end = MatchRun(tokens, start, title.Head);
            if (end < 0)
                return -1;

            if (title.Qualifier.Length > 0)
                return MatchRun(tokens, end, title.Qualifier);

            return end == tokens.Count || IsEdge(tokens[end]) ? end : -1;
        }

        // Any number, such as a track number or a year ("Title 2021 Remaster"), bounds a title like a separator does.
        private static bool IsEdge(string token) => token == Separator || token.All(char.IsDigit);

        // Index just past `words` read in order from `start`, stepping over separators; -1 when they don't follow.
        private static int MatchRun(IReadOnlyList<string> tokens, int start, string[] words)
        {
            int k = start;
            int w = 0;
            while (k < tokens.Count && w < words.Length)
            {
                if (tokens[k] == Separator)
                {
                    k++;
                    continue;
                }

                if (tokens[k] != words[w])
                    return -1;
                k++;
                w++;
            }

            return w == words.Length ? k : -1;
        }

        // Word by word on both sides: "A$AP" is one word, not the stop word "a" and "$ap".
        private static string[] Words(string text, bool keepStopWords) =>
            [.. SlskdItemsParser.NormalizeWords(text).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => keepStopWords || !SlskdItemsParser.IsStopWord(w))];

        // As NormalizeString: a title made only of stop words ("With") keeps them.
        private static bool OnlyStopWords(string text) => Words(text, keepStopWords: true).All(SlskdItemsParser.IsStopWord);

        [GeneratedRegex(@"\s+[-–—]\s+|[-–—()\[\]{},.;:!?/\\~〜·・（）［］｛｝～【】「」『』〈〉《》]|\b(?:feat|ft|featuring)\b", RegexOptions.IgnoreCase)]
        private static partial Regex SeparatorRegex();
    }
}
