using NzbDrone.Plugin.Sleezer.Indexers.Soulseek;

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

/// <summary>
/// Decides whether a file's AcoustID answer says it really is the wanted track.
/// Pure policy — the I/O (fpcalc, AcoustID, DB) lives in PreImportTagger.
/// </summary>
public static class RecordingVerdict
{
    public static FingerprintVerdict Resolve(
        IReadOnlyList<string>? acoustIdRecordingIds,
        string? wantedRecordingId,
        IReadOnlyList<string>? wantedOldRecordingIds,
        string? wantedTitle,
        IReadOnlyDictionary<string, string>? knownRecordings)
    {
        if (acoustIdRecordingIds is not { Count: > 0 })
            return FingerprintVerdict.Unverifiable;

        if (!string.IsNullOrWhiteSpace(wantedRecordingId) &&
            acoustIdRecordingIds.Contains(wantedRecordingId, StringComparer.OrdinalIgnoreCase))
            return FingerprintVerdict.Verified;

        // AcoustID lags MB recording merges, so a correct file often resolves to the
        // old id — accept those too (as Lidarr's own scorer does).
        if (wantedOldRecordingIds is { Count: > 0 } &&
            acoustIdRecordingIds.Intersect(wantedOldRecordingIds, StringComparer.OrdinalIgnoreCase).Any())
            return FingerprintVerdict.Verified;

        // Id equality alone over-rejects: AcoustID often names a different MB
        // recording of the same song, and a wanted id can even 404 upstream. Judge by
        // title instead — different = wrong file, same = ambiguous so defer.
        if (knownRecordings is not { Count: > 0 })
            return FingerprintVerdict.Unverifiable;

        string wanted = NormalizeTitle(wantedTitle);
        bool sawDifferentTitle = false;

        foreach (string id in acoustIdRecordingIds)
        {
            if (!knownRecordings.TryGetValue(id, out string? candidateTitle))
                continue;

            if (NormalizeTitle(candidateTitle) == wanted)
                return FingerprintVerdict.Unverifiable;

            sawDifferentTitle = true;
        }

        return sawDifferentTitle ? FingerprintVerdict.Mismatch : FingerprintVerdict.Unverifiable;
    }

    public static string NormalizeTitle(string? title) =>
        SlskdTextProcessor.StripPunctuation(FeaturedArtistStripper.Strip(title ?? string.Empty)).ToLowerInvariant();
}
