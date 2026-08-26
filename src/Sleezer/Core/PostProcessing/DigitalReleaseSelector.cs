using NzbDrone.Core.Music;

namespace NzbDrone.Plugin.Sleezer.Core.PostProcessing
{
    /// <summary>Ranks an album's Digital Media releases for a storefront download.</summary>
    public static class DigitalReleaseSelector
    {
        // MusicBrainz's format name. The metadata mapping helpers under Metadata/Proxy
        // write the same string; this reads it back off whichever provider supplied it.
        public const string DigitalMediaFormat = "Digital Media";

        /// <summary>True when the release has media and every one of them is Digital Media.</summary>
        public static bool IsDigital(AlbumRelease? release) =>
            release?.Media is { Count: > 0 } media
            && media.All(m => string.Equals(m.Format, DigitalMediaFormat, StringComparison.OrdinalIgnoreCase));

        /// <summary>Digital releases, closest track count first; empty when there are none.</summary>
        public static IReadOnlyList<AlbumRelease> Rank(IEnumerable<AlbumRelease>? releases, int localTrackCount) =>
        [
            .. (releases ?? [])
                .Where(IsDigital)
                // Stable id as the tiebreak so an album never flips between two
                // equally-close digital pressings from one run to the next.
                .OrderBy(r => Math.Abs(r.TrackCount - localTrackCount))
                .ThenBy(r => r.ForeignReleaseId, StringComparer.Ordinal)
        ];
    }
}
