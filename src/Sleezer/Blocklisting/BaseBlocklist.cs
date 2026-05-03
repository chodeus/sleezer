using NzbDrone.Common.Extensions;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Plugin.Sleezer.Blocklisting
{
    public abstract class BaseBlocklist<TProtocol>(IBlocklistRepository blocklistRepository) : IBlocklistForProtocol where TProtocol : IDownloadProtocol
    {
        private readonly IBlocklistRepository _blocklistRepository = blocklistRepository;

        public string Protocol => typeof(TProtocol).Name;

        public bool IsBlocklisted(int artistId, ReleaseInfo release) => _blocklistRepository.BlocklistedByTorrentInfoHash(artistId, release.Guid).Any(b => BaseBlocklist<TProtocol>.SameRelease(b, release));

        // EntityHistory.Data uses PascalCase keys ("Indexer", "Protocol", "Guid",
        // "PublishedDate", "Size" — see EntityHistoryService.Handle(AlbumGrabbedEvent)).
        // The dict is case-sensitive, so reading lowercase keys silently returns
        // null and the blocklist record ends up with empty Indexer/Protocol/
        // TorrentInfoHash, breaking IsBlocklisted matching against future releases.
        public Blocklist GetBlocklist(DownloadFailedEvent message) => new()
        {
            ArtistId = message.ArtistId,
            AlbumIds = message.AlbumIds,
            SourceTitle = message.SourceTitle,
            Quality = message.Quality,
            Date = DateTime.UtcNow,
            PublishedDate = DateTime.TryParse(message.Data.GetValueOrDefault("PublishedDate") ?? string.Empty, out DateTime publishedDate) ? publishedDate : null,
            Size = long.TryParse(message.Data.GetValueOrDefault("Size", "0"), out long size) ? size : 0,
            Indexer = message.Data.GetValueOrDefault("Indexer"),
            Protocol = message.Data.GetValueOrDefault("Protocol"),
            Message = message.Message,
            TorrentInfoHash = message.Data.GetValueOrDefault("Guid")
        };

        // Defensive: item.Indexer can be null on legacy blocklist entries written
        // before the case-sensitivity fix landed. Treat null as "no match" rather
        // than NRE'ing the blocklist lookup.
        private static bool SameRelease(Blocklist item, ReleaseInfo release)
        {
            if (release.Guid.IsNotNullOrWhiteSpace())
                return release.Guid.Equals(item.TorrentInfoHash);
            return item.Indexer != null
                && item.Indexer.Equals(release.Indexer, StringComparison.InvariantCultureIgnoreCase);
        }
    }
}