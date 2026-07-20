using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek.Search.Core;

namespace NzbDrone.Plugin.Sleezer.Indexers.Soulseek.Search.Transformers;

public static partial class QueryNormalizer
{
    // FormD decomposition handles combining accents (é → e) but not these
    // standalone codepoints, which appear verbatim in MusicBrainz names.
    private static readonly Dictionary<char, string> _ligatureMap = new()
    {
        { 'æ', "ae" }, { 'Æ', "Ae" }, { 'œ', "oe" }, { 'Œ', "Oe" },
        { 'ø', "o" }, { 'Ø', "O" }, { 'ð', "d" }, { 'Ð', "D" },
        { 'þ', "th" }, { 'Þ', "Th" }, { 'ß', "ss" }, { 'đ', "d" },
        { 'Đ', "D" }, { 'ł', "l" }, { 'Ł', "L" }, { 'ı', "i" },
    };

    public static SearchContext Normalize(SearchContext context)
    {
        if (!context.QueryType.HasFlag(QueryType.NeedsNormalization))
            return context;

        string? normArtist = NormalizeText(context.Artist);
        string? normAlbum = NormalizeText(context.Album);

        bool artistChanged = !string.Equals(normArtist, context.Artist, StringComparison.Ordinal);
        bool albumChanged = !string.Equals(normAlbum, context.Album, StringComparison.Ordinal);

        if (!artistChanged && !albumChanged)
            return context;

        return context with
        {
            NormalizedArtist = artistChanged ? normArtist : null,
            NormalizedAlbum = albumChanged ? normAlbum : null
        };
    }

    public static string NormalizeText(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return input ?? string.Empty;

        // Map unicode punctuation onto its ASCII cousin BEFORE the strip pass.
        // MusicBrainz uses typographic punctuation ("G‐Eazy" carries U+2010, not
        // '-'); peers share plain-ASCII paths. Deleting these instead of mapping
        // used to fuse adjacent words into tokens no peer path contains
        // ("G‐Eazy" → "GEazy" → zero results, verified against live slskd logs).
        string mapped = MapUnicodePunctuation(input);

        // Decompose accented characters (é → e + ´)
        string decomposed = mapped.Normalize(NormalizationForm.FormD);
        StringBuilder sb = new(decomposed.Length);

        foreach (char c in decomposed)
        {
            if (_ligatureMap.TryGetValue(c, out string? replacement))
            {
                sb.Append(replacement);
                continue;
            }

            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category != UnicodeCategory.NonSpacingMark &&
                category != UnicodeCategory.SpacingCombiningMark &&
                category != UnicodeCategory.EnclosingMark)
            {
                sb.Append(c);
            }
        }

        string result = sb.ToString().Normalize(NormalizationForm.FormC);

        // Apostrophes are intra-word elision: delete ("Don't" → "Dont").
        // Every other stripped character is a separator: replace with a space
        // so the surrounding words stay distinct search terms.
        result = ApostropheRegex().Replace(result, "");
        result = PlusRegex().Replace(result, " ");
        result = PunctuationRegex().Replace(result, " ");
        result = WhitespaceRegex().Replace(result, " ").Trim();

        return result;
    }

    private static string MapUnicodePunctuation(string input)
    {
        StringBuilder sb = new(input.Length);
        foreach (char c in input)
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

    [GeneratedRegex(@"[^\w\s\-&']", RegexOptions.Compiled)]
    private static partial Regex PunctuationRegex();

    [GeneratedRegex(@"'+", RegexOptions.Compiled)]
    private static partial Regex ApostropheRegex();

    [GeneratedRegex(@"\+")]
    private static partial Regex PlusRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
