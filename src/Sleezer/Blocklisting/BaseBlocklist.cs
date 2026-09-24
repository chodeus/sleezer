using NzbDrone.Common.Extensions;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Core.Utilities;

namespace NzbDrone.Plugin.Sleezer.Blocklisting
{
    public abstract class BaseBlocklist<TProtocol>(IBlocklistRepository blocklistRepository) : IBlocklistForProtocol where TProtocol : IDownloadProtocol
    {
        // Lidarr's FailedDownloadService message whenever a user marks a download as failed.
        private const string ManualRemoval = "Manually marked as failed";

        protected readonly IBlocklistRepository BlocklistRepository = blocklistRepository;

        public string Protocol => typeof(TProtocol).Name;

        public virtual bool IsBlocklisted(int artistId, ReleaseInfo release) => MatchingRows(artistId, release).Count != 0;

        // A hand-removed store release blocks every quality tier of its album; an automatic
        // failure keeps to its own tier so Lidarr can fall back to another.
        protected List<Blocklist> MatchingRows(int artistId, ReleaseInfo release)
        {
            string? album = StoreReleaseGuid.AlbumKey(release.Guid);
            return [.. BlocklistRepository.BlocklistedByTorrentInfoHash(artistId, album ?? release.Guid)
                .Where(b => SameRelease(b, release) || (album != null && b.Message == ManualRemoval && StoreReleaseGuid.AlbumKey(b.TorrentInfoHash) == album))];
        }

        // EntityHistory.Data round-trips through a CamelCase DictionaryKeyPolicy
        // (Datastore/Converters/EmbeddedDocumentConverter.cs), so the rehydrated
        // keys handed to GetBlocklist are camelCase — reading PascalCase silently
        // yielded null and wrote empty Indexer/Protocol/hash rows that never
        // matched. Read case-insensitively so either casing works.
        public Blocklist GetBlocklist(DownloadFailedEvent message)
        {
            Dictionary<string, string> data = new(message.Data, StringComparer.OrdinalIgnoreCase);
            return new()
            {
                ArtistId = message.ArtistId,
                AlbumIds = message.AlbumIds,
                SourceTitle = message.SourceTitle,
                Quality = message.Quality,
                Date = DateTime.UtcNow,
                PublishedDate = DateTime.TryParse(data.GetValueOrDefault("PublishedDate") ?? string.Empty, out DateTime publishedDate) ? publishedDate : null,
                Size = long.TryParse(data.GetValueOrDefault("Size", "0"), out long size) ? size : 0,
                Indexer = data.GetValueOrDefault("Indexer"),
                Protocol = data.GetValueOrDefault("Protocol"),
                Message = message.Message,
                TorrentInfoHash = data.GetValueOrDefault("Guid")
            };
        }

        // item.Indexer can be null on legacy rows written before this fix.
        protected static bool SameRelease(Blocklist item, ReleaseInfo release)
        {
            if (release.Guid.IsNotNullOrWhiteSpace())
                return release.Guid.Equals(item.TorrentInfoHash);
            return item.Indexer != null
                && item.Indexer.Equals(release.Indexer, StringComparison.InvariantCultureIgnoreCase);
        }
    }
}
