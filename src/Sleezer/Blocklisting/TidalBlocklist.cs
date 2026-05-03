using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Blocklisting
{
    public class TidalBlocklist : IBlocklistForProtocol
    {
        private readonly IBlocklistRepository _blocklistRepository;

        public TidalBlocklist(IBlocklistRepository blocklistRepository)
        {
            _blocklistRepository = blocklistRepository;
        }

        public string Protocol => nameof(TidalDownloadProtocol);

        public bool IsBlocklisted(int artistId, ReleaseInfo release)
        {
            var blocklistedByTorrentInfohash = _blocklistRepository.BlocklistedByTorrentInfoHash(artistId, release.Guid);
            return blocklistedByTorrentInfohash.Any(b => SameRelease(b, release));
        }

        public Blocklist GetBlocklist(DownloadFailedEvent message)
        {
            // EntityHistory.Data uses PascalCase keys ("Indexer", "Protocol", "Guid",
            // "PublishedDate", "Size"). Lowercase reads silently returned null which
            // fed DateTime.Parse("") → FormatException, crashing the blocklist write.
            // TryParse is defensive even with the case fix in place.
            return new Blocklist
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
        }

        private bool SameRelease(Blocklist item, ReleaseInfo release)
        {
            if (release.Guid.IsNotNullOrWhiteSpace())
            {
                return release.Guid.Equals(item.TorrentInfoHash);
            }

            // Defensive null-check: legacy blocklist entries written before the
            // case-sensitivity fix could have null Indexer.
            return item.Indexer != null
                && item.Indexer.Equals(release.Indexer, StringComparison.InvariantCultureIgnoreCase);
        }
    }
}
