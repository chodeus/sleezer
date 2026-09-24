namespace NzbDrone.Plugin.Sleezer.Blocklisting
{
    /// <summary>Failure reasons that rule out a store album at every quality tier, not just the one grabbed.</summary>
    public static class AlbumWideFailure
    {
        // Lidarr's FailedDownloadService message whenever a user marks a download as failed.
        public const string ManualRemoval = "Manually marked as failed";

        public const string QobuzUnstreamable = "Some tracks are not streamable on this Qobuz account";

        public static bool Covers(string? reason) => reason is ManualRemoval or QobuzUnstreamable;
    }
}
