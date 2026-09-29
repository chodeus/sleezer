using System.Text.Json;
using System.Text.RegularExpressions;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Parser;

namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    /// <summary>Guest artists as a store copy names them, compared with the names a MusicBrainz credit lists.</summary>
    public static partial class GuestCredits
    {
        /// <summary>The copy's guest text: its "(feat. …)" brackets plus main artists other than the searched one.</summary>
        public static string OfStoreCopy(IEnumerable<string?> titles, IEnumerable<string> mainArtists, string searchedArtist)
        {
            string primary = searchedArtist.CleanArtistName();
            IEnumerable<string> featured = titles.SelectMany(t => FeaturedRegex().Matches(t ?? string.Empty))
                .Select(m => m.Groups[1].Value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase);
            IEnumerable<string> others = mainArtists.Where(a => a.CleanArtistName() != primary);
            return string.Join(" & ", featured.Concat(others));
        }

        /// <summary>True when the guest text names exactly these artists and nobody else; the searched artist may appear too.</summary>
        // Names are removed whole, never split on separators: "Tyler, The Creator" is one credited name.
        public static bool NamesExactly(string guestText, IEnumerable<string> names, string searchedArtist)
        {
            string text = Fold(guestText);
            foreach (string name in names.Select(Fold).Where(n => n.Length > 0).OrderByDescending(n => n.Length))
            {
                Match found = WholeName(name).Match(text);
                if (!found.Success)
                    return false;
                text = text.Remove(found.Index, found.Length).Insert(found.Index, " ");
            }

            string primary = Fold(searchedArtist);
            if (primary.Length > 0)
                text = WholeName(primary).Replace(text, " ");

            return SeparatorRegex().Replace(text, string.Empty).Length == 0;
        }

        /// <summary>The credited names in a MusicBrainz release group's artist credit, the primary artist left out.</summary>
        public static IReadOnlyList<string> FromMusicBrainz(string json, string primaryArtistId)
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            return [.. doc.RootElement.GetProperty("artist-credit").EnumerateArray()
                .Where(c => c.GetProperty("artist").GetProperty("id").GetString() != primaryArtistId)
                .Select(c => c.GetProperty("name").GetString() ?? string.Empty)];
        }

        private static string Fold(string value) => Regex.Replace(value.RemoveAccent().ToLowerInvariant(), @"\s+", " ").Trim();

        private static Regex WholeName(string name) => new($@"(?<!\w){Regex.Escape(name)}(?!\w)");

        // Mirrors FeaturedArtistStripper.BracketedFeatPattern plus "with": a credit here, left in titles there.
        [GeneratedRegex(@"[\(\[\{](?:feat\.?|featuring|ft\.?|with)\s+([^\)\]\}]+)[\)\]\}]", RegexOptions.IgnoreCase)]
        private static partial Regex FeaturedRegex();

        // What may remain once every credited name is removed: joining words and punctuation.
        [GeneratedRegex(@"\b(?:and|feat|featuring|ft|with|x)\b|[\s,&.;/+×]")]
        private static partial Regex SeparatorRegex();
    }
}
