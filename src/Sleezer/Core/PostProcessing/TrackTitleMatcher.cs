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
        // Score every viable pair first, then assign best-first: greedy
        // in-array-order assignment let a mediocre earlier file claim the slot
        // an exact later file should have won.
        List<(int Local, int Wanted, int Score)> candidates = [];
        for (int i = 0; i < localTitles.Count; i++)
        {
            string local = Normalize(localTitles[i]);
            if (local.Length == 0)
                continue;

            for (int j = 0; j < wantedTitles.Count; j++)
            {
                string wanted = Normalize(wantedTitles[j]);
                if (wanted.Length == 0 || SlskdTextProcessor.RemixSignaturesConflict(wantedTitles[j], localTitles[i]))
                    continue;

                int score = Fuzz.TokenSetRatio(local, wanted);
                if (score >= threshold)
                    candidates.Add((i, j, score));
            }
        }

        Dictionary<int, int> mapping = [];
        HashSet<int> claimedWanted = [];
        foreach ((int local, int wanted, _) in candidates.OrderByDescending(c => c.Score))
        {
            if (mapping.ContainsKey(local) || claimedWanted.Contains(wanted))
                continue;

            mapping[local] = wanted;
            claimedWanted.Add(wanted);
        }

        return mapping;
    }

    /// <summary>
    /// Derives a comparable title from an extension-less filename: strips the
    /// leading track numbering and any "Artist - " prefix segments.
    /// </summary>
    public static string TitleFromFilename(string? filenameWithoutExtension)
    {
        if (string.IsNullOrWhiteSpace(filenameWithoutExtension))
            return string.Empty;

        string title = LeadingTrackNumberRegex().Replace(filenameWithoutExtension.Trim(), "");
        int dash = title.LastIndexOf(" - ", StringComparison.Ordinal);
        if (dash >= 0 && dash + 3 < title.Length)
            title = title[(dash + 3)..];

        return title.Trim();
    }

    private static string Normalize(string? title) =>
        SlskdTextProcessor.StripPunctuation(title).ToLowerInvariant();

    [GeneratedRegex(@"^\s*\d{1,4}\s*[-._)\]]*\s*")]
    private static partial Regex LeadingTrackNumberRegex();
}
