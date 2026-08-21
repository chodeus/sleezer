using NLog;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;

/// <summary>
/// Copies a grab's non-audio extras (cue/log) into the imported album folder.
/// Lidarr's own ExtraService only imports per-track-basename extras, so
/// album-level rip artifacts never survive import without this.
/// </summary>
public class SlskdExtrasImportService(ISlskdDownloadManager downloadManager, Logger logger) : IHandle<AlbumImportedEvent>
{
    public void Handle(AlbumImportedEvent message)
    {
        if (string.IsNullOrEmpty(message.DownloadId))
            return;

        List<string> trackPaths = message.ImportedTracks
            .Select(t => t.Path)
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList();
        if (trackPaths.Count == 0)
            return;

        try
        {
            downloadManager.ImportExtrasForImportedAlbum(message.DownloadId, trackPaths);
        }
        catch (Exception ex)
        {
            logger.Warn(ex, "Failed to import slskd extra files for download {DownloadId}", message.DownloadId);
        }
    }
}
