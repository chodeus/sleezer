using System.Text.RegularExpressions;

namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    /// <summary>A bracket every track of a store album carries ("(Mixed)", "(Original Soundtrack)"); MusicBrainz track titles leave it out.</summary>
    public static partial class SharedTrackQualifier
    {
        [GeneratedRegex(@"\s*[\(\[]([^\(\)\[\]]+)[\)\]]")]
        private static partial Regex Bracket();

        /// <summary>The titles without the bracket they all share; see <see cref="Removable"/>.</summary>
        public static IReadOnlyList<string> Drop(IReadOnlyList<string> titles, IEnumerable<string?> targetTrackTitles)
        {
            IReadOnlySet<string> removable = Removable(titles, targetTrackTitles);
            return removable.Count == 0 ? titles : [.. titles.Select(t => Without(t, removable))];
        }

        /// <summary>
        /// The brackets every title carries and the target album's tracklists never do.
        /// Empty when the tracklists are unknown or a bracket marks a variant ("(Live)").
        /// </summary>
        // The target decides: MusicBrainz keeps "(Taylor's Version)" and some "[mix cut]" on every track.
        public static IReadOnlySet<string> Removable(IReadOnlyList<string> titles, IEnumerable<string?> targetTrackTitles)
        {
            List<string> targetTitles = [.. targetTrackTitles.OfType<string>()];
            if (titles.Count < 2 || targetTitles.Count == 0)
                return new HashSet<string>();

            HashSet<string> target = new(targetTitles.SelectMany(BracketsOf), StringComparer.OrdinalIgnoreCase);

            HashSet<string> shared = BracketsOf(titles[0]);
            foreach (string title in titles.Skip(1))
                shared.IntersectWith(BracketsOf(title));

            shared.RemoveWhere(inner => target.Contains(inner) || VariantQualifiers.HasVariantQualifier($"({inner})"));
            return shared;
        }

        /// <summary>The title without any of these brackets.</summary>
        // A removed bracket leaves a space, so "First (Mixed)Track" keeps its words apart.
        public static string Without(string title, IReadOnlySet<string> brackets) =>
            Spaces().Replace(Bracket().Replace(title, m => brackets.Contains(m.Groups[1].Value.Trim()) ? " " : m.Value), " ").Trim();

        [GeneratedRegex(@"\s{2,}")]
        private static partial Regex Spaces();

        private static HashSet<string> BracketsOf(string title) =>
            new(Bracket().Matches(title).Select(m => m.Groups[1].Value.Trim()), StringComparer.OrdinalIgnoreCase);
    }
}
