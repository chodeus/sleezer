using System.Text.RegularExpressions;
using NzbDrone.Plugin.Sleezer.Core.Utilities;

namespace NzbDrone.Plugin.Sleezer.Core.PostProcessing;

/// <summary>
/// Title forms the fingerprint verdict reads as one song. Each rule can only turn a refusal into a deferral;
/// the verdict's length check still tells versions apart.
/// </summary>
public static partial class SongTitles
{
    private static readonly HashSet<string> Articles = new(StringComparer.Ordinal) { "the", "a", "an", "and", "of" };

    // Extra qualifier words that name no other recording: "(Acoustic)" is "(Acoustic Version)", "(UK Edit)" is "(UK Radio Edit)".
    private static readonly HashSet<string> Descriptors = new(StringComparer.Ordinal) { "version", "radio" };

    private static readonly HashSet<string> RemixWords = new(StringComparer.Ordinal) { "remix", "mix", "edit", "rework", "vip", "bootleg", "dub", "flip" };

    /// <summary>Whether two titles name one song once framing, articles, a one-letter typo or a compatible qualifier are set aside.</summary>
    public static bool Same(string? heard, string? wanted, Func<string?, string> comparable)
    {
        string a = Unframed(heard), b = Unframed(wanted);
        string keyA = Squashed(comparable(a)), keyB = Squashed(comparable(b));
        if (keyA.Length == 0 || keyB.Length == 0)
            return false;
        if (keyA == keyB || OneLetterApart(keyA, keyB))
            return true;

        if (Squashed(comparable(Base(a))) is not { Length: > 0 } baseA || baseA != Squashed(comparable(Base(b))))
            return false;

        HashSet<string> wordsA = Words(Qualifier(a)), wordsB = Words(Qualifier(b));
        if ((wordsA.Count == 0 && IsSubtitle(b, wordsB)) || (wordsB.Count == 0 && IsSubtitle(a, wordsA)))
            return true;

        // A live recording's place and date are spelled out by one source and dropped by another.
        if (wordsA.Contains("live") && wordsB.Contains("live"))
            return true;

        return Compatible(wordsA, wordsB);
    }

    /// <summary>A plain title against a wanted remix of it: AcoustID files remixes under the original.</summary>
    public static bool PlainForRemix(string? heard, string? wanted, Func<string?, string> comparable)
    {
        string a = Unframed(heard), b = Unframed(wanted);
        return Qualifier(a).Length == 0 && Words(Qualifier(b)).Overlaps(RemixWords) &&
               Squashed(comparable(a)) is { Length: > 0 } plain && plain == Squashed(comparable(Base(b)));
    }

    /// <summary>The title without what frames it: a "(from X)" or " - From X" source, a "Film: " or "Composer: " prefix.</summary>
    internal static string Unframed(string? title)
    {
        string text = ReMixRegex().Replace(title ?? string.Empty, "remix");
        text = FramingBracketRegex().Replace(text, " ");
        text = FromSuffixRegex().Replace(text, string.Empty);
        int colon = text.LastIndexOf(": ", StringComparison.Ordinal);
        if (colon > 0 && text[(colon + 2)..].Trim().Length > 0)
            text = text[(colon + 2)..];
        return text.Trim();
    }

    internal static string Squashed(string comparable) =>
        string.Concat(comparable.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !Articles.Contains(w)));

    internal static bool OneLetterApart(string a, string b)
    {
        if (a.Length < 10 || b.Length < 10 || Math.Abs(a.Length - b.Length) > 1)
            return false;

        int i = 0;
        while (i < Math.Min(a.Length, b.Length) && a[i] == b[i])
            i++;
        return a.Length == b.Length ? a[(i + 1)..] == b[(i + 1)..] : (a.Length > b.Length ? a[(i + 1)..] == b[i..] : a[i..] == b[(i + 1)..]);
    }

    private static string Base(string title) => VariantQualifiers.StripBrackets(DashSuffixRegex().Replace(title, string.Empty));

    private static string Qualifier(string title) =>
        string.Join(" ", BracketRegex().Matches(title).Select(m => m.Value[1..^1]).Append(DashSuffixRegex().Match(title).Value)).Trim(' ', '-', '–', '—');

    private static HashSet<string> Words(string qualifier) =>
        new(ComparableText.Of(qualifier).Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    // "(Be Afraid)": words naming no version. One word may be a remixer named without "remix": "(Moonward)".
    private static bool IsSubtitle(string title, HashSet<string> words) =>
        words.Count >= 2 && !VariantQualifiers.HasVariantQualifier(title) && !words.Overlaps(VersionWords);

    private static bool Compatible(HashSet<string> a, HashSet<string> b)
    {
        (HashSet<string> small, HashSet<string> large) = a.Count <= b.Count ? (a, b) : (b, a);
        if (small.Count == 0 || !small.IsSubsetOf(large))
            return false;

        List<string> extra = [.. large.Except(small)];
        if (extra.All(Descriptors.Contains))
            return true;

        // "(2022 Remix)" names the remix "(2022 Moonward Remix)" does; "(Edit)" is not "(Remix Edit)".
        return extra.Count == 1 && small.Overlaps(RemixWords) && !RemixWords.Contains(extra[0]) && !VersionWords.Contains(extra[0]);
    }

    private static readonly HashSet<string> VersionWords = new(StringComparer.Ordinal)
    {
        "mix", "remix", "edit", "version", "live", "acoustic", "demo", "remaster", "remastered", "instrumental", "extended",
        "radio", "club", "dub", "vip", "cut", "take", "session", "rework", "bootleg", "flip", "unplugged", "orchestral",
        "clean", "dirty", "explicit", "original", "mono", "stereo",
    };

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
