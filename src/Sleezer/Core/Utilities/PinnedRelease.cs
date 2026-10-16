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

        /// <summary>The title a release calls for: its only track's when that is the release title plus a qualifier, else its own; null for no release.</summary>
        // MusicBrainz titles a radio-edit single by its A-side, so "(Radio Edit)" shows only on the track.
        public static string? TitleOf(AlbumRelease? release)
        {
            if (release?.Tracks?.Value is [{ Title: { } track }]
                && VariantQualifiers.HasVariantQualifier(track)
                && StoreReleaseVerifier.SameAlbumTitle(track, release.Title))
                return track;

            return release?.Title;
        }
    }
}
