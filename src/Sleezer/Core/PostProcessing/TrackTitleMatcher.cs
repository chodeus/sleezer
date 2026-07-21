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
        Dictionary<int, int> mapping = [];
        HashSet<int> claimed = [];

        for (int i = 0; i < localTitles.Count; i++)
        {
            string local = Normalize(localTitles[i]);
            if (local.Length == 0)
                continue;

            int best = -1;
            int bestScore = threshold - 1;
            for (int j = 0; j < wantedTitles.Count; j++)
            {
                if (claimed.Contains(j))
                    continue;

                string wanted = Normalize(wantedTitles[j]);
                if (wanted.Length == 0 || SlskdTextProcessor.RemixSignaturesConflict(wantedTitles[j], localTitles[i]))
                    continue;

                int score = Fuzz.TokenSetRatio(local, wanted);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = j;
                }
            }

            if (best >= 0)
            {
                mapping[i] = best;
                claimed.Add(best);
            }
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
