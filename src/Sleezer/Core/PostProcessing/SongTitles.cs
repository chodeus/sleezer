using System.Text.RegularExpressions;
using NzbDrone.Plugin.Sleezer.Core.Utilities;

namespace NzbDrone.Plugin.Sleezer.Core.PostProcessing;

/// <summary>Title forms the fingerprint verdict reads as one song; each can only turn a refusal into a deferral.</summary>
public static partial class SongTitles
{
    private static readonly HashSet<string> Articles = new(StringComparer.Ordinal) { "the", "a", "an", "and", "of" };

    // Extra qualifier words that name no other recording: "(Acoustic)" is "(Acoustic Version)", "(UK Edit)" is "(UK Radio Edit)".
    private static readonly HashSet<string> Descriptors = new(StringComparer.Ordinal) { "version", "radio" };

    private static readonly HashSet<string> RemixWords = new(StringComparer.Ordinal) { "remix", "mix", "edit", "rework", "vip", "bootleg", "dub", "flip" };

    // A reissue's words: "(Remastered 2011)", "(Album Version)", "(Original Mix)", "(Mono)" name the same take.
    private static readonly HashSet<string> ReissueWords = new(StringComparer.Ordinal)
    {
        "remaster", "remastered", "album", "single", "original", "version", "mix", "mono", "stereo", "digital",
    };

    /// <summary>Whether two titles name one song once framing, articles, a one-letter typo or a compatible qualifier are set aside.</summary>
    public static bool Same(string? heard, string? wanted, Func<string?, string> comparable)
    {
        (string a, string b) = WithoutOnePrefix(heard, wanted);
        string keyA = Squashed(comparable(a)), keyB = Squashed(comparable(b));
        if (keyA.Length == 0 || keyB.Length == 0)
            return false;
        if (keyA == keyB || OneLetterApart(Spaced(comparable(a)), Spaced(comparable(b))))
            return true;

        if (!SameBase(a, b, comparable))
            return false;

        HashSet<string> wordsA = Words(Qualifier(a)), wordsB = Words(Qualifier(b));
        if ((wordsA.Count == 0 && IsSubtitle(b, wordsB)) || (wordsB.Count == 0 && IsSubtitle(a, wordsA)))
            return true;

        // A live recording's place and date are spelled out by one source and dropped by another; two places are two shows.
        if ((wordsA.SetEquals(LiveOnly) && wordsB.Contains("live")) || (wordsB.SetEquals(LiveOnly) && wordsA.Contains("live")))
            return true;

        return Compatible(wordsA, wordsB);
    }

    /// <summary>One take under a reissue's title: the qualifiers differ only by remaster, year, album/single/original version or mono/stereo.</summary>
    public static bool Reissue(string? heard, string? wanted, Func<string?, string> comparable)
    {
        (string a, string b) = WithoutOnePrefix(heard, wanted);
        if (!SameBase(a, b, comparable))
            return false;

        HashSet<string> wordsA = Words(Qualifier(a)), wordsB = Words(Qualifier(b));
        return !wordsA.SetEquals(wordsB) && wordsA.Concat(wordsB).Except(wordsA.Intersect(wordsB)).All(w => ReissueWords.Contains(w) || YearRegex().IsMatch(w));
    }

    /// <summary>A plain title against a wanted remix of it: AcoustID files remixes under the original.</summary>
    public static bool PlainForRemix(string? heard, string? wanted, Func<string?, string> comparable)
    {
        string a = Unframed(heard), b = Unframed(wanted);
        return Qualifier(a).Length == 0 && Words(Qualifier(b)).Overlaps(RemixWords) &&
               Squashed(comparable(a)) is { Length: > 0 } plain && plain == Squashed(comparable(Base(b)));
    }

    /// <summary>The title without a "(from X)" or " - From X" source frame.</summary>
    private static string Unframed(string? title)
    {
        string text = ReMixRegex().Replace(title ?? string.Empty, "remix");
        text = FramingBracketRegex().Replace(text, " ");
        return FromSuffixRegex().Replace(text, string.Empty).Trim();
    }

    private static string Squashed(string comparable) => Spaced(comparable).Replace(" ", string.Empty, StringComparison.Ordinal);

    private static string Spaced(string comparable) =>
        string.Join(' ', comparable.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !Articles.Contains(w)));

    // Inside a word and not a digit: "Lanterms" is a typo of "Lanterns"; "Burning" is not "Turning", nor "Part 1" "Part 2".
    private static bool OneLetterApart(string a, string b)
    {
        if (a.Replace(" ", string.Empty, StringComparison.Ordinal).Length < 10 || Math.Abs(a.Length - b.Length) > 1)
            return false;

        int i = 0;
        while (i < Math.Min(a.Length, b.Length) && a[i] == b[i])
            i++;
        if (i == Math.Min(a.Length, b.Length) || i == 0 || a[i - 1] == ' ' || char.IsDigit(a[i]) || char.IsDigit(b[i]))
            return false;
        return a.Length == b.Length ? a[(i + 1)..] == b[(i + 1)..] : (a.Length > b.Length ? a[(i + 1)..] == b[i..] : a[i..] == b[(i + 1)..]);
    }

    // "Composer, Composer: Title" or "Film: Title" against the title framed "(from Film)"; never both titles cut.
    private static (string Heard, string Wanted) WithoutOnePrefix(string? heard, string? wanted)
    {
        string a = Unframed(heard), b = Unframed(wanted);
        if (PrefixFrames(a, wanted))
            return (AfterPrefix(a), b);
        return PrefixFrames(b, heard) ? (a, AfterPrefix(b)) : (a, b);
    }

    private static bool PrefixFrames(string title, string? otherRaw)
    {
        int colon = title.LastIndexOf(": ", StringComparison.Ordinal);
        if (colon <= 0 || title[(colon + 2)..].Trim().Length == 0)
            return false;
        string prefix = title[..colon];
        return prefix.Contains(',', StringComparison.Ordinal) || (otherRaw ?? string.Empty).Contains(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string AfterPrefix(string title)
    {
        int colon = title.LastIndexOf(": ", StringComparison.Ordinal);
        return colon > 0 && title[(colon + 2)..].Trim().Length > 0 ? title[(colon + 2)..] : title;
    }

    private static bool SameBase(string a, string b, Func<string?, string> comparable) =>
        Squashed(comparable(Base(a))) is { Length: > 0 } baseA && baseA == Squashed(comparable(Base(b)));

    private static string Base(string title) => VariantQualifiers.StripBrackets(DashSuffixRegex().Replace(title, string.Empty));

    private static string Qualifier(string title) =>
        string.Join(" ", BracketRegex().Matches(title).Select(m => m.Value[1..^1]).Append(DashSuffixRegex().Match(title).Value)).Trim(' ', '-', '–', '—');

    private static HashSet<string> Words(string qualifier) =>
        new(ComparableText.Of(qualifier).Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    // "(Be Afraid)": words naming no version. One word may be a remixer named without "remix": "(Moonward)"; "(Part 2)" is another track.
    private static bool IsSubtitle(string title, HashSet<string> words) =>
        words.Count >= 2 && !VariantQualifiers.HasVariantQualifier(title) && !words.Overlaps(VersionWords) && !words.Any(w => w.Any(char.IsDigit));

    private static bool Compatible(HashSet<string> a, HashSet<string> b)
    {
        (HashSet<string> small, HashSet<string> large) = a.Count <= b.Count ? (a, b) : (b, a);
        if (small.Count == 0 || !small.IsSubsetOf(large))
            return false;

        List<string> extra = [.. large.Except(small)];
        if (extra.All(Descriptors.Contains))
            return true;

        // "(2022 Remix)" names the remix "(2022 Moonward Remix)" does; a bare "(Remix)" names none, and "(Edit)" is not "(Remix Edit)".
        return extra.Count == 1 && small.Overlaps(RemixWords) && small.Except(RemixWords).Any() &&
               !RemixWords.Contains(extra[0]) && !VersionWords.Contains(extra[0]);
    }

    private static readonly HashSet<string> LiveOnly = new(StringComparer.Ordinal) { "live" };

    private static readonly HashSet<string> VersionWords = new(StringComparer.Ordinal)
    {
        "mix", "remix", "edit", "version", "live", "acoustic", "demo", "remaster", "remastered", "instrumental", "extended",
        "radio", "club", "dub", "vip", "cut", "take", "session", "rework", "bootleg", "flip", "unplugged", "orchestral",
        "clean", "dirty", "explicit", "original", "mono", "stereo", "part", "pt", "no", "vol", "volume", "chapter", "act",
    };

    [GeneratedRegex(@"^(?:19|20)\d\d$")]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"\bre-mix\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReMixRegex();

    [GeneratedRegex(@"\s*[\(\[\{（［｛][^\(\)\[\]\{\}（）［］｛｝]*\bfrom\b[^\(\)\[\]\{\}（）［］｛｝]*[\)\]\}）］｝]", RegexOptions.IgnoreCase)]
    private static partial Regex FramingBracketRegex();

    [GeneratedRegex(@"\s+[-–—]\s+from\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex FromSuffixRegex();

    [GeneratedRegex(@"\s+[-–—]\s+[^-–—]+$")]
    private static partial Regex DashSuffixRegex();

    [GeneratedRegex(@"[\(\[\{（［｛][^\(\)\[\]\{\}（）［］｛｝]*[\)\]\}）］｝]")]
    private static partial Regex BracketRegex();
}
