using NzbDrone.Plugin.Sleezer.Blocklisting;

namespace NzbDrone.Plugin.Sleezer.Core.Qobuz
{
    /// <summary>The reason a failed Qobuz album download gives Lidarr, which becomes its blocklist message.</summary>
    public static class QobuzAlbumFailure
    {
        // Null keeps Lidarr's generic "Failed download detected", which blocks only the grabbed tier.
        public static string? Reason(int unstreamableTracks, bool requireCompleteAlbum) =>
            requireCompleteAlbum && unstreamableTracks > 0 ? AlbumWideFailure.QobuzUnstreamable : null;
    }
}
