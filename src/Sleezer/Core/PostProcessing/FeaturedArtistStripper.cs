using System.Text.RegularExpressions;
using NzbDrone.Common.Extensions;
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

    private static readonly Regex BracketedWithPattern = new(
        @"\s*[\(\[\{]with\s([^\)\]\}]+)[\)\]\}]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex NameSeparatorPattern = new(
        @"\s*(?:,|&|\band\b)\s*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// <see cref="Strip"/>, plus a "(with X)" credit whose every name is one of the credited artists;
    /// any other "(with …)" is title text ("Dancing (With Myself)").
    /// </summary>
    public static string StripCredits(string? input, IEnumerable<string?> creditedArtists)
    {
        string text = Strip(input);
        HashSet<string> credited = [.. creditedArtists.SelectMany(Names)];
        return BracketedWithPattern.Replace(text, m => Names(m.Groups[1].Value).All(credited.Contains) ? string.Empty : m.Value).Trim();
    }

    private static IEnumerable<string> Names(string? text) =>
        NameSeparatorPattern.Split(text ?? string.Empty).Select(n => n.RemoveAccent().Trim().ToLowerInvariant()).Where(n => n.Length > 0);

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

        // Lidarr's CleanTitle drops everything from "feat." on, remix name included, and any "(… Version)".
        info.CleanTitle = info.Title;
        info.ArtistTitle = Strip(info.ArtistTitle);
        info.AlbumTitle = Strip(info.AlbumTitle);
    }
}
