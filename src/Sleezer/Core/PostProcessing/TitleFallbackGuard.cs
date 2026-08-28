using NzbDrone.Core.Music;

namespace NzbDrone.Plugin.Sleezer.Core.PostProcessing
{
    /// <summary>Gates the per-track title fallback that runs when album-level matching fails.</summary>
    public static class TitleFallbackGuard
    {
        /// <summary>True when an album's own type makes title matching trustworthy.</summary>
        public static bool IsEligibleAlbum(Album album)
        {
            bool smallRelease = string.Equals(album.AlbumType, "Single", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(album.AlbumType, "EP", StringComparison.OrdinalIgnoreCase);
            if (!smallRelease)
                return false;

            // A Live/Remix/Demo single's MB TRACK titles are usually plain, so the
            // per-pair variant guard has nothing to compare — a studio file would
            // title-match the live cut. Fail closed for those albums.
            return album.SecondaryTypes?.Any(t => t?.Name is "Live" or "Remix" or "Demo" or "Mixtape") != true;
        }

        /// <summary>True when release-scoped tags may be written to this download by title alone.</summary>
        // A title match proves one track, but Lidarr's writer emits the whole release identity —
        // album_id (5.0) and recording_id (10.0) then outweigh the raw store tags at import.
        public static bool IsSafeTarget(
            AlbumRelease? release,
            int releaseTrackCount,
            int localTrackCount,
            bool preferDigitalMedia)
        {
            if (release == null)
                return false;

            // A storefront download cannot be a CD or vinyl pressing.
            if (preferDigitalMedia && !DigitalReleaseSelector.IsDigital(release))
                return false;

            // TRACKNUMBER/TOTALTRACKS come from the release; a different length writes them wrong.
            return releaseTrackCount == localTrackCount;
        }
    }
}
