using System.Text.RegularExpressions;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek.Search.Core;

namespace NzbDrone.Plugin.Sleezer.Indexers.Soulseek.Search.Transformers;

public static partial class QueryNormalizer
{
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
        string result = ComparableText.FoldAccents(ComparableText.MapPunctuation(input));

        // Apostrophes are intra-word elision: delete ("Don't" → "Dont").
        // Every other stripped character is a separator: replace with a space
        // so the surrounding words stay distinct search terms.
        result = ApostropheRegex().Replace(result, "");
        result = PlusRegex().Replace(result, " ");
        result = PunctuationRegex().Replace(result, " ");
        result = WhitespaceRegex().Replace(result, " ").Trim();

        return result;
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
