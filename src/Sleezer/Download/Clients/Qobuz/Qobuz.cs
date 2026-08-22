using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;
using NzbDrone.Plugin.Sleezer.Core.Utilities;

namespace NzbDrone.Core.Download.Clients.Qobuz
{
    public class Qobuz : DownloadClientBase<QobuzSettings>
    {
        private readonly IQobuzProxy _proxy;

        public Qobuz(IQobuzProxy proxy,
                     IConfigService configService,
                     IDiskProvider diskProvider,
                     IRemotePathMappingService remotePathMappingService,
                     ILocalizationService localizationService,
                     Logger logger)
            : base(configService, diskProvider, remotePathMappingService, localizationService, logger)
        {
            _proxy = proxy;
        }

        public override string Protocol => nameof(QobuzDirectDownloadProtocol);
        public override string Name => "Qobuz";

        public override IEnumerable<DownloadClientItem> GetItems()
        {
            var queue = _proxy.GetQueue(Settings);
            foreach (var item in queue)
                item.DownloadClientInfo = DownloadClientItemClientInfo.FromDownloadClient(this, false);

            // Throttled sweep of empty download shells — the gap per-item cleanup misses
            // (restart / untracked / importFailed). Shared with Deezer and Tidal.
            EmptyDownloadDirectorySweeper.MaybePruneForRoot(
                Settings.DownloadPath,
                queue.Where(i => !i.OutputPath.IsEmpty).Select(i => i.OutputPath.FullPath),
                DateTime.UtcNow,
                _logger);

            return queue;
        }

        public override void RemoveItem(DownloadClientItem item, bool deleteData)
        {
            if (deleteData)
            {
                DeleteItemData(item);
                if (!item.OutputPath.IsEmpty)
                    DownloadFolderCleanup.TryRemoveEmptyParentFolders(item.OutputPath.FullPath, Settings.DownloadPath, "Qobuz", _logger);
            }

            _proxy.RemoveFromQueue(item.DownloadId, Settings);
        }

        public override Task<string> Download(RemoteAlbum remoteAlbum, IIndexer indexer)
            => _proxy.Download(remoteAlbum, Settings);

        public override DownloadClientInfo GetStatus()
            => new()
            {
                IsLocalhost = true,
                OutputRootFolders = new() { new OsPath(Settings.DownloadPath) }
            };

        protected override void Test(List<ValidationFailure> failures)
        {
            // Auth lives on the indexer; here we only verify the download path is usable
            // so typos, missing volumes and bad permissions surface before a download.
            ValidationFailure? failure = TestFolder(Settings.DownloadPath, nameof(Settings.DownloadPath));
            if (failure != null)
                failures.Add(failure);
        }
    }
}
