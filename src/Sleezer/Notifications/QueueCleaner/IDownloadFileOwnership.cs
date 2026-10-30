namespace NzbDrone.Plugin.Sleezer.Notifications.QueueCleaner
{
    /// <summary>A download client whose downloads can share a folder (Soulseek) says which files there are one download's.</summary>
    public interface IDownloadFileOwnership
    {
        /// <summary>Whether the file is the download's; null when the download isn't this client's.</summary>
        bool? Owns(string downloadId, string path);
    }
}
