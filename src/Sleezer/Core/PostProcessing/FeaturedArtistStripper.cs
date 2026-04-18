using System.Text.RegularExpressions;

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
}
