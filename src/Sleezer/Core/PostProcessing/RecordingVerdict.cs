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

        // A bare id comparison over-rejects: AcoustID routinely answers with a
        // DIFFERENT MusicBrainz recording of the same song (per-release entities,
        // "(feat. X)" credit splits), and Lidarr's metadata can even hold a wanted id
        // that no longer resolves upstream — neither can ever match by id. Resolve the
        // returned ids against the artist's own catalogue and judge by title instead.
        // A DIFFERENT title is a real wrong-file signal; the SAME title is only
        // ambiguous (duplicate MB entity, or a distinct same-named cut such as
        // "Voices" with two different collaborators), so it defers rather than
        // green-lighting the file. Version qualifiers survive normalization — only
        // bracketed "(feat. X)" is stripped — so "(Taylor's Version)" reads as
        // different, not the same.
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
