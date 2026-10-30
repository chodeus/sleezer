using NzbDrone.Core.Messaging.Events;
using NzbDrone.Plugin.Sleezer.Notifications.QueueCleaner;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;

/// <summary>Answers Queue Cleaner for the slskd client: which files in a shared folder are a grab's, and their names after a rename.</summary>
public class SlskdGrabFiles(ISlskdDownloadManager downloadManager) : IHandle<DownloadFileRenamedEvent>, IDownloadFileOwnership
{
    public void Handle(DownloadFileRenamedEvent message) => downloadManager.RecordRenamedFile(message.DownloadId, message.OriginalPath, message.NewPath);

    public bool? Owns(string downloadId, string path) => downloadManager.OwnsLocalFile(downloadId, path);
}
