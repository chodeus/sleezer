using NzbDrone.Core.Music;
using NzbDrone.Plugin.Sleezer.Core.Model;

namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    /// <summary>Judges a store copy of an album that already has files against the release those files belong to.</summary>
    public static class HeldReleaseCheck
    {
        // Wider than mastering drift, narrower than a radio edit or extended mix.
        private const int DurationToleranceSeconds = 10;

        public static string? Reason(StoreReleaseInfo release, IReadOnlyList<Track> heldReleaseTracks)
        {
            int held = heldReleaseTracks.Count(t => t.HasFile);
            if (held == 0)
                return null;

            // Mirrors Lidarr's MoreTracksSpecification, which refuses this download at import.
            if (release.TrackCount > 0 && release.TrackCount < held)
                return $"{release.TrackCount} track(s) offered, but {held} of this album's tracks are already on disk";

            // A bigger product is an expansion Lidarr may switch releases for, so only a same-size or smaller one is compared.
            if (release.TrackCount <= 0 || release.TrackCount > heldReleaseTracks.Count)
                return null;

            return UnmatchedTrack(release.TrackDurationsSeconds, heldReleaseTracks);
        }

        private static string? UnmatchedTrack(IReadOnlyList<int>? offered, IReadOnlyList<Track> tracks)
        {
            // A missing length on either side is unjudgeable, never grounds to reject.
            if (offered is not { Count: > 0 } || offered.Any(s => s <= 0) || tracks.Any(t => t.Duration <= 0))
                return null;

            for (int i = 0; i < offered.Count; i++)
            {
                int seconds = offered[i];
                if (!tracks.Any(t => Math.Abs((t.Duration / 1000.0) - seconds) <= DurationToleranceSeconds))
                    return $"track {i + 1} ({seconds / 60}:{seconds % 60:00}) matches no track on the release already on disk";
            }

            return null;
        }
    }
}
