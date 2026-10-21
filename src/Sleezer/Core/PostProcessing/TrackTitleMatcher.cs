using System.Text.RegularExpressions;
using FuzzySharp;
using NzbDrone.Core.Music;
using NzbDrone.Plugin.Sleezer.Core.Utilities;

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
    public static Dictionary<int, int> Match(IReadOnlyList<string?> localTitles, IReadOnlyList<string?> wantedTitles, int threshold = DefaultThreshold) =>
        Match(localTitles, [.. wantedTitles.Select(t => (IReadOnlyList<string?>)[t])], threshold);

    /// <summary>As above, where each wanted track answers to any of its titles: a pair scores by its best-matching title.</summary>
    public static Dictionary<int, int> Match(IReadOnlyList<string?> localTitles, IReadOnlyList<IReadOnlyList<string?>> wantedTitles, int threshold = DefaultThreshold)
    {
        string[] locals = [.. localTitles.Select(Normalize)];

        List<(int Local, int Wanted, int Gate, int TieBreak)> candidates = [];
        for (int i = 0; i < locals.Length; i++)
        {
            if (locals[i].Length == 0)
                continue;

            for (int j = 0; j < wantedTitles.Count; j++)
            {
                if (wantedTitles[j].Select(w => Score(localTitles[i], locals[i], w, threshold)).Where(s => s != null).Max() is { } best)
                    candidates.Add((i, j, best.Gate, best.TieBreak));
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

    private static (int Gate, int TieBreak)? Score(string? localTitle, string local, string? wantedTitle, int threshold)
    {
        string wanted = Normalize(wantedTitle);
        if (wanted.Length == 0 || VariantQualifiers.RemixSignaturesConflict(wantedTitle, localTitle))
            return null;

        // TokenSetRatio saturates at 100 for ANY token superset, so it stays
        // the accept gate but cannot rank: TokenSortRatio breaks the ties
        // (subset pairs collapse well below 100), a differing digit-token pair
        // is a different song ("Part 1" vs "Part 12" scores 92), and two or
        // more leftover local tokens mean an unknown qualifier — fail closed
        // so future variant vocabulary ("sped up" of tomorrow) never scores.
        if (DigitTokensDiffer(local, wanted) || NamesAnotherPart(local, wanted) || LeftoverTokenCount(local, wanted) >= 2)
            return null;

        int gate = Fuzz.TokenSetRatio(local, wanted);
        return gate >= threshold ? (gate, Fuzz.TokenSortRatio(local, wanted)) : null;
    }

    /// <summary>
    /// Each wanted track's own title, then every other title Lidarr holds for its recording.
    /// A title two of the tracks' recordings share places neither, so it is left out.
    /// </summary>
    public static List<IReadOnlyList<string?>> TitlesOf(IReadOnlyList<Track> wanted, IReadOnlyDictionary<string, IReadOnlyList<KnownTrack>> knownRecordings)
    {
        List<string[]> others = [.. wanted.Select(t => OtherTitles(t, knownRecordings))];
        HashSet<string> shared = [.. wanted
            .SelectMany((t, i) => others[i].Append(t.Title).Select(title => (Title: Normalize(title), Owner: t.ForeignRecordingId ?? $"#{i}")))
            .Distinct()
            .GroupBy(x => x.Title)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)];
        return [.. wanted.Select((t, i) => (IReadOnlyList<string?>)[t.Title, .. others[i].Where(title => !shared.Contains(Normalize(title)))])];
    }

    private static string[] OtherTitles(Track track, IReadOnlyDictionary<string, IReadOnlyList<KnownTrack>> knownRecordings) =>
        track.ForeignRecordingId is { } id && knownRecordings.TryGetValue(id, out IReadOnlyList<KnownTrack>? known)
            ? [.. known.Select(k => k.Title).Where(title => title != track.Title).Distinct()]
            : [];

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
        // Numbers are track, disc or year fields, not qualifiers; DigitTokensDiffer and NamesAnotherPart judge them.
        return local.Split(' ', StringSplitOptions.RemoveEmptyEntries).Count(t => !wantedTokens.Contains(t) && !t.All(char.IsDigit));
    }

    // "Take 2", "Part II": a numbered take or part is another track, unless the wanted title numbers that part too.
    private static bool NamesAnotherPart(string local, string wanted)
    {
        HashSet<string> parts = PartNumbers(local, withLastWord: false);
        return parts.Count > 0 && !parts.IsSubsetOf(PartNumbers(wanted, withLastWord: true));
    }

    // Numbers after a numbering word ("part 2", "take two"); withLastWord also a number ending the title ("Freestyle 1").
    private static HashSet<string> PartNumbers(string title, bool withLastWord)
    {
        string[] tokens = [.. title.Split(' ', StringSplitOptions.RemoveEmptyEntries).SelectMany(SplitGluedPart)];
        HashSet<string> numbers = [];
        for (int i = 1; i < tokens.Length; i++)
        {
            if (NumberingWords.Contains(tokens[i - 1]) && NumberValue(tokens[i]) is { } number)
                numbers.Add(number);
        }

        if (withLastWord && tokens.Length > 0 && NumberValue(tokens[^1]) is { } last)
            numbers.Add(last);
        return numbers;
    }

    // "part2", "pt2", "take2": a numbering word glued to its number reads as both.
    private static IEnumerable<string> SplitGluedPart(string token)
    {
        int digits = token.Length - token.Reverse().TakeWhile(char.IsDigit).Count();
        return digits > 0 && digits < token.Length && NumberingWords.Contains(token[..digits]) ? [token[..digits], token[digits..]] : [token];
    }

    private static readonly HashSet<string> NumberingWords = ["part", "parts", "pt", "take", "set", "vol", "volume", "chapter", "act", "no", "number", "book", "movement", "mvt"];

    private static readonly Dictionary<string, string> NumberWords = new(StringComparer.Ordinal)
    {
        ["one"] = "1", ["two"] = "2", ["three"] = "3", ["four"] = "4", ["five"] = "5",
        ["six"] = "6", ["seven"] = "7", ["eight"] = "8", ["nine"] = "9", ["ten"] = "10",
        ["eleven"] = "11", ["twelve"] = "12", ["thirteen"] = "13", ["fourteen"] = "14", ["fifteen"] = "15",
        ["sixteen"] = "16", ["seventeen"] = "17", ["eighteen"] = "18", ["nineteen"] = "19", ["twenty"] = "20",
        ["i"] = "1", ["ii"] = "2", ["iii"] = "3", ["iv"] = "4", ["v"] = "5",
        ["vi"] = "6", ["vii"] = "7", ["viii"] = "8", ["ix"] = "9", ["x"] = "10",
        ["xi"] = "11", ["xii"] = "12", ["xiii"] = "13", ["xiv"] = "14", ["xv"] = "15",
        ["xvi"] = "16", ["xvii"] = "17", ["xviii"] = "18", ["xix"] = "19", ["xx"] = "20",
    };

    // "2", "02", "two" and "ii" all read "2"; null for a word that is no number.
    private static string? NumberValue(string token) =>
        token.Length > 0 && token.All(char.IsDigit) ? token.TrimStart('0') : NumberWords.GetValueOrDefault(token);

    /// <summary>
    /// Derives a comparable title from an extension-less filename: strips the
    /// leading track numbering, any "Artist - " prefix segments, an "Album [01-06] "
    /// prefix and "Artist_Album_02_" fields.
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

        title = BracketedIndexPrefixRegex().Replace(title, "");
        return WithoutUnderscoreField(title).Trim();
    }

    // Where spaces separate words, "_02_" separates fields; an underscore inside a title often stands in for / ? * or ".
    private static string WithoutUnderscoreField(string title)
    {
        if (!title.Contains(' '))
            return title;

        string rest = UnderscoreTrackFieldPrefixRegex().Replace(title, "");
        return ClosesUnopenedBracket(rest) ? title : rest;
    }

    // "(Live 12_04_99)": a date inside brackets is not a field.
    private static bool ClosesUnopenedBracket(string text) => text.Count(c => c is ')' or ']') > text.Count(c => c is '(' or '[');

    private static string Normalize(string? title) => RecordingVerdict.NormalizeTitle(title);

    [GeneratedRegex(@"^\s*\d{1,4}\s*[-._)\]]*\s*")]
    private static partial Regex LeadingTrackNumberRegex();

    // "[02]" or "[01-06]" with a title after it; a one-digit "[2]" can be part of the title.
    [GeneratedRegex(@"^.*\[(?:\d{2,3}|\d{1,3}-\d{1,3})\]\s*(?=\S)")]
    private static partial Regex BracketedIndexPrefixRegex();

    [GeneratedRegex(@"^.*_\d{1,3}(?:-\d{1,3})?_(?=.)")]
    private static partial Regex UnderscoreTrackFieldPrefixRegex();
}
