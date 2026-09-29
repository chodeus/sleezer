using NzbDrone.Core.Music;
using NzbDrone.Plugin.Sleezer.Core.Utilities;

namespace NzbDrone.Plugin.Sleezer.Core.PostProcessing
{
    /// <summary>Gates the per-track title fallback that runs when album-level matching fails.</summary>
    public static class TitleFallbackGuard
    {
        /// <summary>True when an album's type, and a remix single's track titles, make title matching trustworthy.</summary>
        public static bool IsEligibleAlbum(Album album, IReadOnlyCollection<string?> trackTitles)
        {
            bool smallRelease = string.Equals(album.AlbumType, "Single", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(album.AlbumType, "EP", StringComparison.OrdinalIgnoreCase);
            if (!smallRelease)
                return false;

            // Live/Remix/Demo singles can carry plain MB TRACK titles, so the variant
            // guard can't tell a studio file from the live cut — fail closed.
            if (album.SecondaryTypes?.Any(t => t?.Name is "Live" or "Demo" or "Mixtape") == true)
                return false;

            // A remix track that names its remix is safe: RemixSignaturesConflict rejects a plain file against it.
            return album.SecondaryTypes?.Any(t => t?.Name == "Remix") != true ||
                   (trackTitles.Count > 0 && trackTitles.All(t => VariantQualifiers.ExtractRemixSignature(t) != null));
        }

        /// <summary>True when release-scoped tags may be written to this download by title alone.</summary>
        // A title match proves one track, but Lidarr's writer emits the whole release identity —
        // album_id (5.0) and recording_id (10.0) then outweigh the raw store tags at import.
        public static bool IsSafeTarget(AlbumRelease? release, int localTrackCount, bool preferDigitalMedia)
        {
            // A storefront download cannot be a CD or vinyl pressing.
            if (preferDigitalMedia && !DigitalReleaseSelector.IsDigital(release))
                return false;

            return DigitalReleaseSelector.FitsDownload(release, localTrackCount);
        }
    }
}
