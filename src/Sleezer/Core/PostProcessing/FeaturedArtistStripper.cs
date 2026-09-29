using System.Text.RegularExpressions;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Plugin.Sleezer.Core.PostProcessing;

/// <summary>
/// Strips bracketed featured-artist suffixes from track titles / artist names.
/// Only handles bracketed forms — `Foo (feat. Bar)`, `Foo [featuring Bar]`,
/// `Foo {ft Bar}`. Bare-text forms (`Foo feat. Bar`) are intentionally left
/// alone in v1: too easy to chew through legitimate text like "feat" inside
/// a song name.
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
    public static void ForIdentification(ParsedTrackInfo info, string? primaryArtist)
    {
        // Comma-joined guest credits drag Lidarr's artist distance below the import cutoff.
        info.ArtistTitle = StripGuestCredits(info.ArtistTitle, primaryArtist) ?? info.ArtistTitle;

        // MusicBrainz credits a featured artist, never the title, so "Foo (feat. Bar)" must match "Foo".
        info.Title = Strip(info.Title);
        info.CleanTitle = Strip(info.CleanTitle);
        info.ArtistTitle = Strip(info.ArtistTitle);
        info.AlbumTitle = Strip(info.AlbumTitle);
    }
}
