using System.Text.RegularExpressions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Core.Utilities;

namespace NzbDrone.Plugin.Sleezer.Core.PostProcessing;

/// <summary>
/// Strips featured-artist credits from track titles / artist names.
/// <see cref="Strip"/> handles bracketed forms only — `Foo (feat. Bar)`, `Foo [featuring Bar]`,
/// `Foo {ft Bar}`. Bare-text forms (`Foo feat. Bar`) are stripped only where known names
/// anchor them: unanchored, they chew through legitimate text like "feat" inside a song name.
/// </summary>
public static class FeaturedArtistStripper
{
    private static readonly Regex BracketedFeatPattern = new(
        @"\s*[\(\[\{](?:feat\.?|featuring|ft\.?)\s[^\)\]\}]*[\)\]\}]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string Strip(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return input ?? string.Empty;

        string cleaned = BracketedFeatPattern.Replace(input, string.Empty);
        return cleaned.Trim();
    }

    private static readonly Regex BracketedWithPattern = new(
        @"\s*[\(\[\{]with\s([^\)\]\}]+)[\)\]\}]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex OnlySeparatorsPattern = new(
        @"^(?:\s|,|&|\band\b)*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// <see cref="Strip"/>, plus a "(with X)" credit naming credited artists, whole or as listed;
    /// any other "(with …)" is title text ("Dancing (With Myself)").
    /// </summary>
    public static string StripCredits(string? input, IEnumerable<string?> creditedArtists)
    {
        string text = Strip(input);
        // Read only once a "(with …)" turns up: the list can cost a MusicBrainz lookup.
        Lazy<string[]> credited = new(() => FoldedCredits(creditedArtists));
        return BracketedWithPattern.Replace(text, m => NamesOnly(m.Groups[1].Value, credited.Value) ? string.Empty : m.Value).Trim();
    }

    private static readonly Regex BareFeatPattern = new(
        @"\s+(?:feat\.|ft\.|featuring)\s+(?<names>[^()\[\]{}]+?)(?=\s+[-‐‑‒–—―−]\s+|\s*[\(\[\{]|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>A bare "Foo feat. X" credit removed when X names only credited artists.</summary>
    // Ends at a dash or a bracket, so "Song feat. X (Y remix)" keeps its remix.
    public static string StripBareCredits(string text, IEnumerable<string?> creditedArtists) =>
        BareFeatPattern.Replace(text, m => NamesOnlyCredited(m.Groups["names"].Value, creditedArtists) ? string.Empty : m.Value);

    /// <summary>True when the text names credited artists, whole or as listed, and nothing else but separators.</summary>
    public static bool NamesOnlyCredited(string text, IEnumerable<string?> creditedArtists) => NamesOnly(text, FoldedCredits(creditedArtists));

    // Credited entries split only on commas (joined tags list artists that way), so "Simon & Garfunkel" never credits "Simon".
    private static string[] FoldedCredits(IEnumerable<string?> creditedArtists) =>
        [.. creditedArtists.OfType<string>().SelectMany(a => a.Split(',').Prepend(a)).Select(Fold).Where(n => n.Length > 0).Distinct().OrderByDescending(n => n.Length)];

    // Whole credited names are removed, longest first; a credit leaves only separators behind.
    private static bool NamesOnly(string clause, IEnumerable<string> credited)
    {
        string rest = Fold(clause);
        foreach (string name in credited)
            rest = Regex.Replace(rest, $@"(?<!\w){Regex.Escape(name)}(?!\w)", " ");
        return rest != Fold(clause) && OnlySeparatorsPattern.IsMatch(rest);
    }

    // Optional marks only, as GuestCredits: on kana or Cyrillic й the mark spells another name.
    // Punctuation mapped and spaces collapsed first: MusicBrainz credits "Moon’n’Harbour" where a title writes "Moon'n'Harbour".
    private static string Fold(string name) => Regex.Replace(ComparableText.FoldOptionalMarks(ComparableText.MapPunctuation(name)).ToLowerInvariant(), @"\s+", " ").Trim();

    private static readonly Regex GuestCreditSeparatorPattern = new(
        @"^(?:\s*[,;]|\s+(?:feat\.?|featuring|ft\.?)\s)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Strips comma/semicolon/bare-feat guest credits only when the value
    /// starts with the known primary artist — the anchor is what makes the
    /// bare-text form safe here, unlike <see cref="Strip"/>.
    /// </summary>
    public static string? StripGuestCredits(string? input, string? primaryArtist)
    {
        if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(primaryArtist))
            return input;

        string trimmed = input.Trim();
        string artist = primaryArtist.Trim();
        if (trimmed.Length <= artist.Length ||
            !trimmed.StartsWith(artist, StringComparison.OrdinalIgnoreCase))
            return input;

        return GuestCreditSeparatorPattern.IsMatch(trimmed[artist.Length..])
            ? trimmed[..artist.Length]
            : input;
    }

    /// <summary>Drops guest credits from read tags before matching; the tags on disk are untouched.</summary>
    // creditedArtists: names a "(with …)" may credit besides the file's own artist tag.
    public static void ForIdentification(ParsedTrackInfo info, string? primaryArtist, IEnumerable<string?> creditedArtists)
    {
        IEnumerable<string?> credited = creditedArtists.Prepend(info.ArtistTitle);

        // Comma-joined guest credits drag Lidarr's artist distance below the import cutoff.
        info.ArtistTitle = StripGuestCredits(info.ArtistTitle, primaryArtist) ?? info.ArtistTitle;

        // MusicBrainz credits a featured artist, never the title, so "Foo (feat. Bar)" must match "Foo".
        info.Title = StripCredits(info.Title, credited);

        // Lidarr's CleanTitle drops everything from "feat." on, remix name included, and any "(… Version)".
        info.CleanTitle = info.Title;
        info.ArtistTitle = Strip(info.ArtistTitle);
        info.AlbumTitle = StripCredits(info.AlbumTitle, credited);
    }
}
