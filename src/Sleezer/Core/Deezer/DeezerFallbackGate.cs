using System;

namespace NzbDrone.Plugin.Sleezer.Core.Deezer
{
    /// <summary>Identity gate for Deezer track substitution — accepts only the same recording.</summary>
    public static class DeezerFallbackGate
    {
        private const int DurationToleranceSeconds = 3;

        public static bool Accept(FallbackCandidate original, FallbackCandidate candidate, out string reason)
        {
            var originalIsrc = original.Isrc?.Trim();
            var candidateIsrc = candidate.Isrc?.Trim();

            if (!string.IsNullOrEmpty(originalIsrc) && !string.IsNullOrEmpty(candidateIsrc))
            {
                if (string.Equals(originalIsrc, candidateIsrc, StringComparison.OrdinalIgnoreCase))
                {
                    reason = "same ISRC";
                    return true;
                }

                // A different ISRC is a different recording (clean edit, re-record) — never substitute it.
                reason = $"ISRC mismatch ({originalIsrc} vs {candidateIsrc})";
                return false;
            }

            if (!TitleEquals(original.Title, candidate.Title))
            {
                reason = $"title mismatch ('{original.Title}' vs '{candidate.Title}')";
                return false;
            }

            if (!TitleEquals(original.Version, candidate.Version))
            {
                reason = $"version mismatch ('{original.Version}' vs '{candidate.Version}')";
                return false;
            }

            if (Math.Abs(original.DurationSeconds - candidate.DurationSeconds) > DurationToleranceSeconds)
            {
                reason = $"duration mismatch ({original.DurationSeconds}s vs {candidate.DurationSeconds}s)";
                return false;
            }

            if (original.Explicit != candidate.Explicit)
            {
                reason = "explicit flag mismatch";
                return false;
            }

            reason = "no ISRC on record; title, version, duration and explicit flag all match";
            return true;
        }

        private static bool TitleEquals(string? a, string? b) =>
            string.Equals(a?.Trim() ?? string.Empty, b?.Trim() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    public readonly record struct FallbackCandidate(string? Isrc, string? Title, string? Version, int DurationSeconds, bool Explicit);
}
