using NzbDrone.Core.Music;
using NzbDrone.Plugin.Sleezer.Core.Model;

namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    /// <summary>Judges a store copy of an album that already has files against the album's releases.</summary>
    public static class HeldReleaseCheck
    {
        // Wider than mastering drift, narrower than a radio edit or extended mix.
        private const int DurationToleranceSeconds = 10;

        // The switch StoreReleaseVerifier honours; settings from another plugin or none at all count as strict.
        public static bool StrictMatching(object? indexerSettings) =>
            indexerSettings is not IStoreMatchingSettings { StrictMatching: false };

        public static string? Reason(StoreReleaseInfo release, IReadOnlyList<Track> albumTracks)
        {
            if (!albumTracks.Any(t => t.HasFile))
                return null;

            // A missing length on either side is unjudgeable, never grounds to reject.
            IReadOnlyList<int>? offered = release.TrackDurationsSeconds;
            if (offered is not { Count: > 0 } || offered.Any(s => s <= 0) || albumTracks.Any(t => t.Duration <= 0))
                return null;

            // Any release may be the one the import switches to, so a track only has to exist somewhere on the album.
            for (int i = 0; i < offered.Count; i++)
            {
                int seconds = offered[i];
                if (!albumTracks.Any(t => Math.Abs((t.Duration / 1000.0) - seconds) <= DurationToleranceSeconds))
                    return $"track {i + 1} ({seconds / 60}:{seconds % 60:00}) matches no track on any release of this album";
            }

            return null;
        }
    }
}
