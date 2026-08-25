using NzbDrone.Core.Music;

namespace NzbDrone.Plugin.Sleezer.Core.PostProcessing
{
    /// <summary>
    /// Ranks the Digital Media releases of an album for a download that came from a digital
    /// store, where the product cannot be a CD or vinyl pressing. Lidarr ranks candidates by
    /// track-count distance alone, so a CD pressing that ties wins — and the pick is then
    /// written into MUSICBRAINZ_ALBUMID, which steers every later import.
    /// </summary>
    public static class DigitalReleaseSelector
    {
        // MusicBrainz's format name. The metadata mapping helpers under Metadata/Proxy
        // write the same string; this reads it back off whichever provider supplied it.
        public const string DigitalMediaFormat = "Digital Media";

        /// <summary>True when the release has media and every one of them is Digital Media.</summary>
        public static bool IsDigital(AlbumRelease? release) =>
            release?.Media is { Count: > 0 } media
            && media.All(m => string.Equals(m.Format, DigitalMediaFormat, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Every Digital Media release, closest track count first. Empty when MusicBrainz
        /// holds no digital release for the album — the albums that want a Harmony import.
        /// </summary>
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
