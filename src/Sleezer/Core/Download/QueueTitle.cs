namespace NzbDrone.Plugin.Sleezer.Core.Download
{
    public static class QueueTitle
    {
        /// <summary>The title a store client reports for a queue item: the grab's own, else the store's.</summary>
        // Lidarr maps a queue item by parsing this title and only reads the grab when that finds nothing (TrackedDownloadService).
        public static string For(string? releaseTitle, string storeTitle) =>
            string.IsNullOrWhiteSpace(releaseTitle) ? storeTitle : releaseTitle;
    }
}
