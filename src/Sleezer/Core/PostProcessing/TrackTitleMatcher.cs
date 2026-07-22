using System.Text.RegularExpressions;
using FuzzySharp;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek;

namespace NzbDrone.Plugin.Sleezer.Core.PostProcessing;

/// <summary>
/// Title-driven track matching for downloads whose album-level identification
/// cannot pass — a single pulled out of an album share carries that album's
/// tag, so album distance always fails while the track itself is exactly right.
/// </summary>
public static partial class TrackTitleMatcher
{
    public const int DefaultThreshold = 85;

    /// <summary>
    /// Greedy unique assignment of local titles onto wanted track titles. A
    /// pair only matches at or above the similarity threshold and with
    /// agreeing remix/variant signatures. Returns local→wanted indices for the
    /// files that matched; unmatched files are simply absent.
    /// </summary>
    public static Dictionary<int, int> Match(IReadOnlyList<string?> localTitles, IReadOnlyList<string?> wantedTitles, int threshold = DefaultThreshold)
    {
        string[] locals = [.. localTitles.Select(Normalize)];
        string[] wanted = [.. wantedTitles.Select(Normalize)];

        // TokenSetRatio saturates at 100 for ANY token superset, so it stays
        // the accept gate but cannot rank: TokenSortRatio breaks the ties
        // (subset pairs collapse well below 100), a differing digit-token pair
        // is a different song ("Part 1" vs "Part 12" scores 92), and two or
        // more leftover local tokens mean an unknown qualifier — fail closed
        // so future variant vocabulary ("sped up" of tomorrow) never scores.
        List<(int Local, int Wanted, int Gate, int TieBreak)> candidates = [];
        for (int i = 0; i < locals.Length; i++)
        {
            if (locals[i].Length == 0)
                continue;

            for (int j = 0; j < wanted.Length; j++)
            {
                if (wanted[j].Length == 0 || SlskdTextProcessor.RemixSignaturesConflict(wantedTitles[j], localTitles[i]))
                    continue;
                if (DigitTokensDiffer(locals[i], wanted[j]) || LeftoverTokenCount(locals[i], wanted[j]) >= 2)
                    continue;

                int gate = Fuzz.TokenSetRatio(locals[i], wanted[j]);
                if (gate >= threshold)
                    candidates.Add((i, j, gate, Fuzz.TokenSortRatio(locals[i], wanted[j])));
            }
        }

        Dictionary<int, int> mapping = [];
        HashSet<int> claimedWanted = [];
        foreach ((int local, int wantedIndex, _, _) in candidates
                     .OrderByDescending(c => c.Gate)
                     .ThenByDescending(c => c.TieBreak)
                     .ThenBy(c => c.Local)
                     .ThenBy(c => c.Wanted))
        {
            if (mapping.ContainsKey(local) || claimedWanted.Contains(wantedIndex))
                continue;

            mapping[local] = wantedIndex;
            claimedWanted.Add(wantedIndex);
        }

        return mapping;
    }

    private static bool DigitTokensDiffer(string a, string b)
    {
        HashSet<string> da = DigitTokens(a);
        HashSet<string> db = DigitTokens(b);
        return da.Count > 0 && db.Count > 0 && !da.SetEquals(db);
    }

    private static HashSet<string> DigitTokens(string title) => title
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Where(t => t.All(char.IsDigit))
        .Select(t => t.TrimStart('0'))
        .ToHashSet(StringComparer.Ordinal);

    private static int LeftoverTokenCount(string local, string wanted)
    {
        HashSet<string> wantedTokens = wanted.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        return local.Split(' ', StringSplitOptions.RemoveEmptyEntries).Count(t => !wantedTokens.Contains(t));
    }

    /// <summary>
    /// Derives a comparable title from an extension-less filename: strips the
    /// leading track numbering and any "Artist - " prefix segments.
    /// </summary>
    public static string TitleFromFilename(string? filenameWithoutExtension)
    {
        if (string.IsNullOrWhiteSpace(filenameWithoutExtension))
            return string.Empty;

        string trimmed = filenameWithoutExtension.Trim();
        string title = LeadingTrackNumberRegex().Replace(trimmed, "");

        // "1999.flac": when the whole name IS digits it is a title, not a
        // track index.
        if (title.Length == 0)
            return trimmed;

        int dash = title.LastIndexOf(" - ", StringComparison.Ordinal);
        if (dash >= 0 && dash + 3 < title.Length)
            title = title[(dash + 3)..];

        return title.Trim();
    }

    private static string Normalize(string? title) =>
        SlskdTextProcessor.StripPunctuation(FeaturedArtistStripper.Strip(title ?? string.Empty)).ToLowerInvariant();

    [GeneratedRegex(@"^\s*\d{1,4}\s*[-._)\]]*\s*")]
    private static partial Regex LeadingTrackNumberRegex();
}
