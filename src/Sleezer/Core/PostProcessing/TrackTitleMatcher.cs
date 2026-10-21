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
        Match(localTitles, [.. wantedTitles.Select(t => new WantedTitles(t, []))], threshold);

    /// <summary>As above; a file also matches a track whose recording carries the file's exact title on another release.</summary>
    public static Dictionary<int, int> Match(IReadOnlyList<string?> localTitles, IReadOnlyList<WantedTitles> wanted, int threshold = DefaultThreshold)
    {
        string[] locals = [.. localTitles.Select(Normalize)];
        HashSet<string>[] others = [.. wanted.Select(w => w.OtherTitles.Select(Normalize).ToHashSet(StringComparer.Ordinal))];

        List<(int Local, int Wanted, int Gate, int TieBreak, bool OwnTitle)> candidates = [];
        for (int i = 0; i < locals.Length; i++)
        {
            if (locals[i].Length == 0)
                continue;

            for (int j = 0; j < wanted.Count; j++)
            {
                (int Gate, int TieBreak)? own = Score(localTitles[i], locals[i], wanted[j].Title, threshold);

                // Other titles match exactly: a fuzzy one would draw in files of the release's other recordings.
                if ((others[j].Contains(locals[i]) ? (100, 100) : own) is { } best)
                    candidates.Add((i, j, best.Gate, best.TieBreak, own == best));
            }
        }

        Dictionary<int, int> mapping = [];
        HashSet<int> claimedWanted = [];
        // An exact own title outranks an exact other title: a recording listed twice keeps each file at its own position.
        foreach ((int local, int wantedIndex, _, _, _) in candidates
                     .OrderByDescending(c => c.Gate)
                     .ThenByDescending(c => c.TieBreak)
                     .ThenByDescending(c => c.OwnTitle)
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

    /// <summary>The names a release supplies: its credited artists and its title.</summary>
    public sealed record ReleaseNames(IEnumerable<string?> CreditedArtists, string? AlbumTitle);

    /// <summary>
    /// As above; then a file still unmatched matches by its title without the release's names
    /// ("Song (with Artist)", "Artist - Song", "Song - Album"), to an exact title only.
    /// </summary>
    public static Dictionary<int, int> Match(IReadOnlyList<string?> localTitles, IReadOnlyList<WantedTitles> wanted, ReleaseNames names, int threshold = DefaultThreshold)
    {
        Dictionary<int, int> mapping = Match(localTitles, wanted, threshold);
        HashSet<int> claimed = [.. mapping.Values];
        HashSet<string> trackTitles = [.. wanted.Select(w => Normalize(w.Title))];
        // Read only for a file left unmatched: the list can cost a MusicBrainz lookup.
        Lazy<string?[]> credited = new(() => [.. names.CreditedArtists]);

        for (int i = 0; i < localTitles.Count; i++)
        {
            string local = Normalize(localTitles[i]);
            if (mapping.ContainsKey(i) || local.Length == 0)
                continue;

            // A title without its names is a guess: exact only, any track's own title before its other titles, the least cut first.
            string[] cuts = [.. WithoutReleaseNames(localTitles[i], Credited(), names.AlbumTitle, trackTitles).Select(Normalize).Where(s => s.Length > 0 && s != local).Distinct()];
            int found = cuts.Select(s => FirstUnclaimed(j => Normalize(wanted[j].Title) == s)).FirstOrDefault(j => j >= 0, -1);
            if (found < 0)
                found = cuts.Select(s => FirstUnclaimed(j => wanted[j].OtherTitles.Any(o => Normalize(o) == s))).FirstOrDefault(j => j >= 0, -1);
            if (found < 0)
                continue;

            mapping[i] = found;
            claimed.Add(found);
        }

        return mapping;

        int FirstUnclaimed(Func<int, bool> matches) => Enumerable.Range(0, wanted.Count).FirstOrDefault(j => !claimed.Contains(j) && matches(j), -1);

        IEnumerable<string?> Credited()
        {
            foreach (string? artist in credited.Value)
                yield return artist;
        }
    }

    // The title after each cut: "(with X)" and "feat. X", then "X - " and " - X" or " - Album" peeled from either end.
    // The last part left stays, and so does a part that is one of the release's titles, as it may be the title.
    private static IEnumerable<string> WithoutReleaseNames(string? title, IEnumerable<string?> credited, string? albumTitle, IReadOnlySet<string> trackTitles)
    {
        string text = FeaturedArtistStripper.StripBareCredits(FeaturedArtistStripper.StripCredits(title, credited), credited);
        yield return text;

        MatchCollection dashes = AffixDashRegex().Matches(text);
        int head = 0, tail = dashes.Count - 1, from = 0, to = text.Length;
        for (; head <= tail && IsReleaseName(text[from..dashes[head].Index], credited, null, trackTitles); head++)
        {
            from = dashes[head].Index + dashes[head].Length;
            yield return text[from..to];
        }

        for (; tail >= head && IsReleaseName(text[(dashes[tail].Index + dashes[tail].Length)..to], credited, albumTitle, trackTitles); tail--)
        {
            to = dashes[tail].Index;
            yield return text[from..to];
        }
    }

    private static bool IsReleaseName(string affix, IEnumerable<string?> credited, string? albumTitle, IReadOnlySet<string> trackTitles)
    {
        string name = Normalize(affix);
        return name.Length > 0 && !trackTitles.Contains(name) &&
               (name == Normalize(albumTitle) || FeaturedArtistStripper.NamesOnlyCredited(affix, credited));
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
        if (DigitTokensDiffer(local, wanted) || NamesAnotherPart(local, wanted) || LeftoverTokenCount(local, wanted) >= 2 || MissesTitleWords(local, wantedTitle))
            return null;

        int gate = Fuzz.TokenSetRatio(local, wanted);
        return gate >= threshold ? (gate, Fuzz.TokenSortRatio(local, wanted)) : null;
    }

    /// <summary>A wanted track's own title, and the other titles its recording carries on other releases.</summary>
    public sealed record WantedTitles(string? Title, IReadOnlyList<string> OtherTitles);

    /// <summary>
    /// Each wanted track's own title and every other title Lidarr holds for its recording. An other
    /// title that a second recording of the release also carries, as its own or another, is left out.
    /// </summary>
    public static List<WantedTitles> TitlesOf(IReadOnlyList<Track> wanted, IReadOnlyDictionary<string, IReadOnlyList<KnownTrack>> knownRecordings)
    {
        List<string[]> known = [.. wanted.Select(t => t.ForeignRecordingId is { } id && knownRecordings.TryGetValue(id, out IReadOnlyList<KnownTrack>? tracks)
            ? tracks.Select(k => k.Title).Distinct().ToArray()
            : [])];
        HashSet<string> shared = [.. wanted
            .SelectMany((t, i) => known[i].Append(t.Title).Select(title => (Title: Normalize(title), Owner: t.ForeignRecordingId?.ToLowerInvariant() ?? $"#{i}")))
            .Distinct()
            .GroupBy(x => x.Title)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)];
        return [.. wanted.Select((t, i) => new WantedTitles(
            t.Title,
            [.. known[i].Where(title => title != t.Title && !shared.Contains(Normalize(title)) && NamesTheVariant(t.Title, title))]))];
    }

    // A plain title never stands in for a variant: a compilation's plain "Song" for a remix single would name any "Song".
    private static bool NamesTheVariant(string? own, string other) =>
        !VariantQualifiers.HasVariantQualifier(own) || VariantQualifiers.HasVariantQualifier(other);

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

    // Two words of the wanted title proper that the file lacks name another song ("Fire" is not "Line of Fire"); a bracketed
    // qualifier may go unsaid, and a file named for one half of the wanted's dash ("Song", "Moss Remix") lost the other.
    private static bool MissesTitleWords(string local, string? wantedTitle)
    {
        if (string.IsNullOrEmpty(wantedTitle))
            return false;

        Match dash = AffixDashRegex().Match(wantedTitle);
        if (dash.Success && (Normalize(wantedTitle[..dash.Index]) == local || Normalize(wantedTitle[(dash.Index + dash.Length)..]) == local))
            return false;

        HashSet<string> localTokens = local.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        string[] words = Normalize(RecordingVerdict.QualifierBracketRegex().Replace(wantedTitle, " ")).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        HashSet<string> missing = new(StringComparer.Ordinal);
        for (int i = 0; i < words.Length; i++)
        {
            // Words the file writes as one are there ("Antihero").
            if (JoinedRun(words, i, localTokens) is > 0 and int run)
                i += run - 1;
            else if (!localTokens.Contains(words[i]) && !Numbering(words, i))
                missing.Add(words[i]);
        }

        return missing.Count >= 2;
    }

    // A part number is NamesAnotherPart's to judge ("Pt. 2" is "Part II"); "One" in "One More Time" is a title word.
    private static bool Numbering(string[] words, int i) =>
        words[i].All(char.IsDigit) || NumberingWords.Contains(words[i]) || (i > 0 && NumberingWords.Contains(words[i - 1]) && NumberValue(words[i]) != null);

    // How many words from `start` the file joins into one of its own, "anti hero" as "antihero"; 0 when none.
    private static int JoinedRun(string[] words, int start, HashSet<string> localTokens)
    {
        string joined = words[start];
        for (int end = start + 1; end < words.Length; end++)
        {
            joined += words[end];
            if (localTokens.Contains(joined))
                return end - start + 1;
        }

        return 0;
    }

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

    // A spaced dash of any kind ("Song – Artist"), never the hyphen inside "Anti-D".
    [GeneratedRegex(@"\s+[-‐‑‒–—―−]\s+")]
    private static partial Regex AffixDashRegex();
}
