using System.Text.Json;
using System.Text.RegularExpressions;
using NzbDrone.Core.Parser;

namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    /// <summary>Guest artists as a store copy names them and as MusicBrainz credits them, in comparable form.</summary>
    public static partial class GuestCredits
    {
        /// <summary>Guests in the title's "(feat. …)" brackets plus main artists other than the searched one.</summary>
        public static HashSet<string> OfStoreCopy(string? title, IEnumerable<string> mainArtists, string searchedArtist)
        {
            IEnumerable<string> featured = FeaturedRegex().Matches(title ?? string.Empty).SelectMany(m => SplitNames(m.Groups[1].Value));
            return Clean(featured.Concat(mainArtists.SelectMany(SplitNames)), searchedArtist);
        }

        /// <summary>Names comparable across stores and MusicBrainz, the searched artist left out.</summary>
        public static HashSet<string> Clean(IEnumerable<string> names, string searchedArtist)
        {
            string primary = searchedArtist.CleanArtistName();
            return [.. names.Select(n => n.CleanArtistName()).Where(n => n.Length > 0 && n != primary)];
        }

        /// <summary>The credited names in a MusicBrainz release group's artist credit, the primary artist left out.</summary>
        public static IReadOnlyList<string> FromMusicBrainz(string json, string primaryArtistId)
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            return [.. doc.RootElement.GetProperty("artist-credit").EnumerateArray()
                .Where(c => c.GetProperty("artist").GetProperty("id").GetString() != primaryArtistId)
                .Select(c => c.GetProperty("name").GetString() ?? string.Empty)];
        }

        private static IEnumerable<string> SplitNames(string names) => SeparatorRegex().Split(names).Where(n => !string.IsNullOrWhiteSpace(n));

        // Mirrors FeaturedArtistStripper.BracketedFeatPattern plus "with": a credit here, left in titles there.
        [GeneratedRegex(@"[\(\[\{](?:feat\.?|featuring|ft\.?|with)\s+([^\)\]\}]+)[\)\]\}]", RegexOptions.IgnoreCase)]
        private static partial Regex FeaturedRegex();

        [GeneratedRegex(@"\s*(?:,|&|\band\b)\s*", RegexOptions.IgnoreCase)]
        private static partial Regex SeparatorRegex();
    }
}
