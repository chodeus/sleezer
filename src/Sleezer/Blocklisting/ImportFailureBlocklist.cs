using NLog;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Plugin.Sleezer.Blocklisting
{
    /// <summary>Blocklists a Soulseek release whose import failed, so a later search doesn't grab the same files again.</summary>
    public class ImportFailureBlocklist(IBlocklistRepository blocklistRepository, Logger logger) : IHandle<AlbumImportIncompleteEvent>
    {
        public void Handle(AlbumImportIncompleteEvent message)
        {
            // The queue item stays for a manual import; only the next search must look elsewhere.
            TrackedDownload download = message.TrackedDownload;
            RemoteAlbum? remoteAlbum = download.RemoteAlbum;
            if (download.Protocol != nameof(SoulseekDownloadProtocol) || remoteAlbum?.Release?.Guid == null || remoteAlbum.Artist == null)
                return;

            if (new SoulseekBlocklist(blocklistRepository).IsPermanentlyBlocklisted(remoteAlbum.Artist.Id, remoteAlbum.Release))
                return;

            blocklistRepository.Insert(SoulseekBlocklist.ImportFailed(remoteAlbum));
            logger.Info("Blocklisted '{Title}' because its import failed; a later search will look for another source", remoteAlbum.Release.Title);
        }
    }
}
