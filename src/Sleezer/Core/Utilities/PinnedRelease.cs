using NzbDrone.Core.Music;

namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    /// <summary>The one release Lidarr imports for an album with Any Release OK off.</summary>
    public static class PinnedRelease
    {
        /// <summary>The album's only monitored release when Any Release OK is off; null otherwise.</summary>
        // Lidarr refuses a file matched to any other release ("Album release not requested").
        public static AlbumRelease? Of(Album? album)
        {
            if (album == null || album.AnyReleaseOk)
                return null;

            List<AlbumRelease>? monitored = album.AlbumReleases?.Value?.Where(r => r.Monitored).ToList();
            return monitored is { Count: 1 } ? monitored[0] : null;
        }
    }
}
