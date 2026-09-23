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

        /// <summary>True when the release is exactly as long as the download.</summary>
        // TRACKTOTAL and album_id come from the release; a different length writes them wrong,
        // and album_id (5.0) then locks Lidarr's importer onto that edition.
        public static bool FitsDownload(AlbumRelease? release, int localTrackCount) =>
            release != null && release.TrackCount == localTrackCount;

        /// <summary>Digital releases of the download's length, in stable id order; empty when none fit.</summary>
        public static IReadOnlyList<AlbumRelease> Rank(IEnumerable<AlbumRelease>? releases, int localTrackCount) =>
        [
            .. (releases ?? [])
                .Where(IsDigital)
                .Where(r => FitsDownload(r, localTrackCount))
                // Stable id order so an album never flips between two fitting
                // digital pressings from one run to the next.
                .OrderBy(r => r.ForeignReleaseId, StringComparer.Ordinal)
        ];
    }
}
