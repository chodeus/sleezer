using NzbDrone.Common.Messaging;

namespace NzbDrone.Plugin.Sleezer.Notifications.QueueCleaner
{
    /// <summary>Queue Cleaner renamed one of a download's files.</summary>
    public class DownloadFileRenamedEvent(string downloadId, string originalPath, string newPath) : IEvent
    {
        public string DownloadId { get; } = downloadId;

        public string OriginalPath { get; } = originalPath;

        public string NewPath { get; } = newPath;
    }
}
