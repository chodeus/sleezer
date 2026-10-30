using NzbDrone.Core.Messaging.Events;
using NzbDrone.Plugin.Sleezer.Notifications.QueueCleaner;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;

/// <summary>Tells the slskd client about Queue Cleaner's renames, so removing the grab still deletes the renamed files.</summary>
public class SlskdRenameRecorder(ISlskdDownloadManager downloadManager) : IHandle<DownloadFileRenamedEvent>
{
    public void Handle(DownloadFileRenamedEvent message) => downloadManager.RecordRenamedFile(message.DownloadId, message.OriginalPath, message.NewPath);
}
