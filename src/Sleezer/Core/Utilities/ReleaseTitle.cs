using System.Text.RegularExpressions;
using NzbDrone.Core.Parser;
using NzbDrone.Plugin.Sleezer.Core.Model;

namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    /// <summary>The artist, album and trailing tags of a store release title.</summary>
    public sealed record ReleaseTitleParts(string Artist, string Album, string Tail)
    {
        public string Text => $"{Artist} - {Album}{Tail}";
    }

    /// <summary>Builds release titles whose album Lidarr's title parser reads back whole.</summary>
    // Lidarr's ReportAlbumTitleRegex (Parser.cs) ends the album at the first "(" or "["; "{" passes through.
    public static class ReleaseTitle
    {
        private static readonly Regex BracketPair = new(@"[\(\[](?<inner>[^\(\)\[\]]*)[\)\]]", RegexOptions.Compiled);
        private static readonly Regex FeatCredit = new(@"^\s*(?:feat\.?|ft\.?|featuring)\s", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // The dot is required bare: "Little Feat", "Ft Worth".
        private static readonly Regex BareFeat = new(@"\s+(?:feat\.|ft\.|featuring)\s+[^{}]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex TrailingWord = new(@"\s*\b\w+\W*$", RegexOptions.Compiled);
        private static readonly Regex TrailingNonWord = new(@"\W+$", RegexOptions.Compiled);
        private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

        /// <summary>A store's album name. A featured-artist credit moves to a trailing "(feat. …)" that Lidarr drops, as MusicBrainz titles omit it.</summary>
        public static string StoreAlbum(string? album)
        {
            if (string.IsNullOrWhiteSpace(album))
                return string.Empty;

            List<string> credits = [];
            string text = BracketPair.Replace(album, m =>
            {
                string inner = m.Groups["inner"].Value.Trim();
                if (!FeatCredit.IsMatch(inner))
                    return $"{{{inner}}}";
                credits.Add(inner);
                return string.Empty;
            });

            // A bare credit counts only outside every bracket; inside one it is part of that text.
            text = Braced(text);
            if (BareFeat.Matches(text).FirstOrDefault(m => OutsideBraces(text, m.Index)) is { } bare)
            {
                credits.Add(bare.Value.Trim());
                text = text[..bare.Index] + " " + text[(bare.Index + bare.Length)..];
            }

            text = Collapse(text);
            return credits.Count == 0 ? text : $"{text} ({string.Join(", ", credits)})";
        }

        /// <summary>A searched MusicBrainz title: every bracket becomes a brace, so its clean title equals the album's.</summary>
        public static string SearchedAlbum(string album)
        {
            string encoded = Collapse(Braced(BracketPair.Replace(album, m => $"{{{m.Groups["inner"].Value.Trim()}}}")));
            string wanted = album.CleanArtistName();

            // Lidarr's parse drops the closing brace, and CleanArtistName keeps a final "of"/"a"/"the" that
            // it drops before a bracket: "Songs (Best Of)" would clean to "songsbestof", not "songsbest".
            while (AsParsed(encoded).CleanArtistName() != wanted && TrailingWord.Match(encoded) is { Success: true, Index: > 0 } last
                   && AsParsed(encoded[..last.Index] + "}").CleanArtistName() == wanted)
                encoded = encoded[..last.Index] + "}";

            return encoded;
        }

        // What ReportAlbumTitleRegex leaves of an album followed by " (year)".
        private static string AsParsed(string album) => TrailingNonWord.Replace(album, string.Empty);

        public static void Compose(StoreReleaseInfo release, string? artist, string? album, string tail) =>
            Render(release, new ReleaseTitleParts(artist ?? string.Empty, StoreAlbum(album), tail));

        public static void Render(StoreReleaseInfo release, ReleaseTitleParts parts)
        {
            release.TitleParts = parts;
            release.Title = parts.Text;
        }

        /// <summary>Presents a verified result as the searched artist and album; false when it has no composed parts.</summary>
        public static bool AsSearchedAlbum(StoreReleaseInfo release, string searchedArtist, string searchedTitle)
        {
            if (release.TitleParts is not { } parts || string.IsNullOrWhiteSpace(searchedArtist) || string.IsNullOrWhiteSpace(searchedTitle))
                return false;

            // The store's own wording stays, after the year, for custom formats and the UI.
            string original = $"{release.Artist} - {release.CandidateTitle ?? release.Album}";
            string tail = original.CleanArtistName() == $"{searchedArtist} - {searchedTitle}".CleanArtistName() ? parts.Tail : $"{parts.Tail} [{original}]";

            Render(release, new ReleaseTitleParts(searchedArtist, SearchedAlbum(searchedTitle), tail));
            return true;
        }

        private static bool OutsideBraces(string text, int index) => text[..index].Count(c => c == '{') == text[..index].Count(c => c == '}');

        // Unpaired openers would still end the album early.
        private static string Braced(string text) => text.Replace('(', '{').Replace('[', '{').Replace(')', '}').Replace(']', '}');

        private static string Collapse(string text) => Spaces.Replace(text, " ").Trim();
    }
}
