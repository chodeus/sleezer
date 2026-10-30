using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;

namespace NzbDrone.Plugin.Sleezer.Blocklisting
{
    public class SoulseekBlocklist(IBlocklistRepository blocklistRepository) : BaseBlocklist<SoulseekDownloadProtocol>(blocklistRepository)
    {
        // Only failures inside this window count toward the 1h → 6h → 24h tier, so
        // an intermittently failing peer decays back to 1h instead of pinning at 24h.
        private static readonly TimeSpan EscalationWindow = TimeSpan.FromDays(30);

        // Lidarr's message when you remove or fail a download yourself, and any failed import's (ours, Queue Cleaner's):
        // neither is a peer that may come back, so neither decays like a transfer failure does.
        internal const string ManualFailureMessage = "Manually marked as failed";
        internal const string ImportFailedMessage = "Import failed: grabbing the same files again would fail the same way";
        private const string ImportFailedPrefix = "Import failed";

        public override bool IsBlocklisted(int artistId, ReleaseInfo release)
        {
            List<Blocklist> matching = MatchingRows(artistId, release);
            if (matching.Any(IsPermanent))
                return true;

            DateTime cutoff = DateTime.UtcNow - EscalationWindow;
            List<Blocklist> rows = [.. matching.Where(r => r.Date >= cutoff)];
            if (rows.Count == 0)
                return false;

            DateTime newest = rows.Max(r => r.Date);
            return DateTime.UtcNow - newest < SlskdDownloadItem.RetryBackoffWindow(rows.Count);
        }

        internal bool IsPermanentlyBlocklisted(int artistId, ReleaseInfo release) => MatchingRows(artistId, release).Any(IsPermanent);

        private static bool IsPermanent(Blocklist row) =>
            row.Message == ManualFailureMessage || row.Message?.StartsWith(ImportFailedPrefix, StringComparison.Ordinal) == true;

        // GetBlocklist's row, keyed on the guid as every Soulseek row is; Lidarr's own Block() leaves the key empty for non-torrents.
        internal static Blocklist ImportFailed(RemoteAlbum remoteAlbum) => new()
        {
            ArtistId = remoteAlbum.Artist.Id,
            AlbumIds = [.. remoteAlbum.Albums.Select(a => a.Id)],
            SourceTitle = remoteAlbum.Release.Title,
            Quality = remoteAlbum.ParsedAlbumInfo?.Quality,
            Date = DateTime.UtcNow,
            PublishedDate = remoteAlbum.Release.PublishDate,
            Size = remoteAlbum.Release.Size,
            Indexer = remoteAlbum.Release.Indexer,
            Protocol = nameof(SoulseekDownloadProtocol),
            Message = ImportFailedMessage,
            TorrentInfoHash = remoteAlbum.Release.Guid
        };
    }

    public class QobuzBlocklist(IBlocklistRepository blocklistRepository) : BaseBlocklist<QobuzDownloadProtocol>(blocklistRepository)
    { }

    public class DeezerBlocklist(IBlocklistRepository blocklistRepository) : BaseBlocklist<DeezerDownloadProtocol>(blocklistRepository)
    { }

    public class TidalBlocklist(IBlocklistRepository blocklistRepository) : BaseBlocklist<TidalDownloadProtocol>(blocklistRepository)
    { }

    public class SubSonicBlocklist(IBlocklistRepository blocklistRepository) : BaseBlocklist<SubSonicDownloadProtocol>(blocklistRepository)
    { }

    public class BandcampBlocklist(IBlocklistRepository blocklistRepository) : BaseBlocklist<BandcampDownloadProtocol>(blocklistRepository)
    { }
}