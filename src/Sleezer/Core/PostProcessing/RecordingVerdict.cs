using System.Text.RegularExpressions;
using NzbDrone.Core.Music;
using NzbDrone.Plugin.Sleezer.Core.Utilities;

namespace NzbDrone.Plugin.Sleezer.Core.PostProcessing;

/// <summary>
/// Unverifiable (no fpcalc / AcoustID down / not indexed / ambiguous) is distinct
/// from Mismatch: callers fall back on Unverifiable and reject only on Mismatch, so
/// a blip can never block every import.
/// </summary>
public enum FingerprintVerdict
{
    Verified,
    Mismatch,
    Unverifiable
}

/// <summary>A title and length Lidarr files a recording under; one recording sits on many releases.</summary>
public sealed record KnownTrack(string Title, int DurationMs);

/// <summary>The track a file should be: its recording, title, length and the artists a "(with X)" in its title may name.</summary>
public sealed record WantedRecording(string? Id, IReadOnlyList<string>? OldIds, string? Title, int DurationMs, IEnumerable<string?> CreditedArtists);

/// <summary>
/// Decides whether a file's AcoustID answer says it really is the wanted track.
/// Pure policy — the I/O (fpcalc, AcoustID, DB) lives in PreImportTagger.
/// </summary>
public static partial class RecordingVerdict
{
    private const int FitsWantedMs = 3000;
    private const int SameAudioMs = 10000;

    /// <param name="musicBrainzOnly">Recordings AcoustID named that Lidarr holds nowhere, as MusicBrainz files them; they can only defer. Read once, up to the first match.</param>
    public static FingerprintVerdict Resolve(
        IReadOnlyList<string>? acoustIdRecordingIds,
        TimeSpan fileDuration,
        WantedRecording wanted,
        IReadOnlyDictionary<string, IReadOnlyList<KnownTrack>>? knownRecordings,
        IEnumerable<KnownTrack>? musicBrainzOnly = null)
    {
        if (acoustIdRecordingIds is not { Count: > 0 })
            return FingerprintVerdict.Unverifiable;

        if (!string.IsNullOrWhiteSpace(wanted.Id) &&
            acoustIdRecordingIds.Contains(wanted.Id, StringComparer.OrdinalIgnoreCase))
            return FingerprintVerdict.Verified;

        // AcoustID lags MB recording merges, so a correct file often resolves to the
        // old id — accept those too (as Lidarr's own scorer does).
        if (wanted.OldIds is { Count: > 0 } &&
            acoustIdRecordingIds.Intersect(wanted.OldIds, StringComparer.OrdinalIgnoreCase).Any())
            return FingerprintVerdict.Verified;

        // Id equality alone over-rejects: AcoustID often names a different MB
        // recording of the same song, and a wanted id can even 404 upstream. Judge by
        // title instead — different = wrong file, same = ambiguous so defer.
        if (knownRecordings is not { Count: > 0 })
            return FingerprintVerdict.Unverifiable;

        string wantedTitle = Comparable(wanted.Title, wanted.CreditedArtists);
        string wantedSong = Comparable(WithoutUnheardCut(wanted.Title), wanted.CreditedArtists);
        bool fitsWanted = fileDuration > TimeSpan.Zero && wanted.DurationMs > 0 &&
                          Math.Abs(fileDuration.TotalMilliseconds - wanted.DurationMs) <= FitsWantedMs;
        bool sawDifferentTitle = false;
        bool differentTitleHeard = false;

        // AcoustID hears an instrumental, clean, explicit or dirty cut as the same audio as the others.
        bool NamesWanted(KnownTrack t) =>
            Comparable(t.Title, wanted.CreditedArtists) == wantedTitle ||
            (fitsWanted && Comparable(WithoutUnheardCut(t.Title), wanted.CreditedArtists) == wantedSong);

        foreach (string id in acoustIdRecordingIds)
        {
            if (!knownRecordings.TryGetValue(id, out IReadOnlyList<KnownTrack>? tracks) || tracks.Count == 0)
                continue;

            if (tracks.Any(NamesWanted))
                return FingerprintVerdict.Unverifiable;

            sawDifferentTitle = true;
            differentTitleHeard |= tracks.Any(t => t.DurationMs <= 0 || Math.Abs(t.DurationMs - fileDuration.TotalMilliseconds) <= SameAudioMs);
        }

        // Only a mis-tagged submission links audio to a recording this far off its length: AcoustID files remixes under the original.
        if (sawDifferentTitle && fitsWanted && !differentTitleHeard)
            return FingerprintVerdict.Unverifiable;

        // MusicBrainz's titles only defer: measured on real imports, refusing by them refused right files.
        return sawDifferentTitle && musicBrainzOnly?.Any(NamesWanted) != true ? FingerprintVerdict.Mismatch : FingerprintVerdict.Unverifiable;
    }

    /// <summary>Every title and length Lidarr holds for the named recordings, across all the releases they sit on.</summary>
    public static Dictionary<string, IReadOnlyList<KnownTrack>> KnownTracks(IEnumerable<string> recordingIds, IEnumerable<Track> tracks)
    {
        HashSet<string> named = new(recordingIds, StringComparer.OrdinalIgnoreCase);
        return tracks
            .Where(t => !string.IsNullOrWhiteSpace(t.Title) && t.ForeignRecordingId != null && named.Contains(t.ForeignRecordingId))
            .GroupBy(t => t.ForeignRecordingId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<KnownTrack>)[.. g.Select(t => new KnownTrack(t.Title, t.Duration))], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Each recording AcoustID named, with the titles and lengths the verdict judged it by, for the mismatch log.</summary>
    public static string DescribeMatches(IReadOnlyList<string>? acoustIdRecordingIds, IReadOnlyDictionary<string, IReadOnlyList<KnownTrack>>? knownRecordings) =>
        acoustIdRecordingIds is not { Count: > 0 }
            ? "no recordings"
            : string.Join("; ", acoustIdRecordingIds.Select(id =>
                knownRecordings != null && knownRecordings.TryGetValue(id, out IReadOnlyList<KnownTrack>? tracks) && tracks.Count > 0
                    ? $"{id} {string.Join(" / ", tracks.Select(t => $"\"{t.Title}\"").Distinct())} ({string.Join("/", tracks.Select(t => $"{t.DurationMs / 1000}s").Distinct())})"
                    : $"{id} (not in Lidarr)"));

    public static string NormalizeTitle(string? title) =>
        ComparableText.Of(FeaturedArtistStripper.Strip(title ?? string.Empty));

    // Not NormalizeTitle: title matching shares that one, and must keep "(Remixer Remix)" apart from "(Remixer)".
    private static string Comparable(string? title, IEnumerable<string?> creditedArtists)
    {
        string stripped = FeaturedArtistStripper.StripCredits(title, creditedArtists);
        string comparable = ComparableText.Of(WithoutRemixWord(ComparableText.MapPunctuation(stripped)));
        return comparable.Length > 0 ? comparable : ComparableText.MarksOnly(stripped);
    }

    // "(Moonward)" names the remix "(Moonward Remix)" does; "(Remix)", "(Extended Remix)" and "(Remix Edit)" still name a cut.
    private static string WithoutRemixWord(string title)
    {
        string bracketed = QualifierBracketRegex().Replace(title, m => m.Value[0] + DropRemixWord(m.Value[1..^1]) + m.Value[^1]);
        int dash = bracketed.LastIndexOf(" - ", StringComparison.Ordinal);
        return dash < 0 ? bracketed : bracketed[..(dash + 3)] + DropRemixWord(bracketed[(dash + 3)..]);
    }

    private static string DropRemixWord(string qualifier) =>
        TrailingRemixWordRegex().Match(qualifier) is { Success: true } match && IsRemixerName(match.Groups["name"].Value)
            ? match.Groups["name"].Value
            : qualifier;

    private static bool IsRemixerName(string name) =>
        !MixDescriptionRegex().IsMatch(name) && !VariantQualifiers.HasVariantQualifier($"({name})");

    private static string WithoutUnheardCut(string? title) => UnheardCutRegex().Replace(ComparableText.MapPunctuation(title ?? string.Empty), " ");

    [GeneratedRegex(@"[\(\[\{（［｛][^\(\)\[\]\{\}（）［］｛｝]*[\)\]\}）］｝]")]
    internal static partial Regex QualifierBracketRegex();

    [GeneratedRegex(@"^\s*(?<name>.*\S)\s+(?:re)?mix\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingRemixWordRegex();

    // Words that describe a mix where VariantQualifiers sees no variant: "(Radio Mix)" is no remixer's.
    [GeneratedRegex(@"\b(?:radio|club|original|vocal|version|album|single|main|short|long|full|dance|video|alternate|alternative|bonus|the)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MixDescriptionRegex();

    [GeneratedRegex(@"\s*(?:[\(\[\{（［｛]\s*(?:instrumental|clean|explicit|dirty)(?:\s+version)?\s*[\)\]\}）］｝]|\s-\s(?:instrumental|clean|explicit|dirty)(?:\s+version)?\s*$)", RegexOptions.IgnoreCase)]
    private static partial Regex UnheardCutRegex();
}
