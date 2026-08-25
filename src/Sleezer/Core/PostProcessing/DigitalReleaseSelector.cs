using NzbDrone.Core.Music;

namespace NzbDrone.Plugin.Sleezer.Core.PostProcessing
{
    /// <summary>
    /// Picks a Digital Media release for a download that came from a digital store.
    /// Lidarr ranks candidates by track-count distance alone, and CD pressings usually
    /// outnumber digital ones at the same track count, so a store download lands on a CD
    /// release — which is then written into MUSICBRAINZ_ALBUMID and steers every later import.
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
        /// The digital release closest in track count to what actually downloaded, or null
        /// when the current pick is already digital or nothing digital is on offer.
        /// </summary>
        public static AlbumRelease? Choose(IEnumerable<AlbumRelease>? releases, AlbumRelease? current, int localTrackCount)
        {
            if (IsDigital(current))
                return null;

            return releases?
                .Where(IsDigital)
                .Where(r => current == null || !string.Equals(r.ForeignReleaseId, current.ForeignReleaseId, StringComparison.Ordinal))
                // Stable id as the tiebreak so an album never flips between two
                // equally-close digital pressings from one run to the next.
                .OrderBy(r => Math.Abs(r.TrackCount - localTrackCount))
                .ThenBy(r => r.ForeignReleaseId, StringComparer.Ordinal)
                .FirstOrDefault();
        }
    }
}
