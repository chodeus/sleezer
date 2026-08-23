using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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

namespace NzbDrone.Core.Download.Clients.Tidal
{
    public class Tidal : DownloadClientBase<TidalSettings>
    {
        private readonly ITidalProxy _proxy;

        public Tidal(ITidalProxy proxy,
                     IConfigService configService,
                     IDiskProvider diskProvider,
                     IRemotePathMappingService remotePathMappingService,
                     ILocalizationService localizationService,
                     Logger logger)
            : base(configService, diskProvider, remotePathMappingService, localizationService, logger)
        {
            _proxy = proxy;
        }

        public override string Protocol => nameof(TidalDownloadProtocol);
        public override string Name => "Tidal";

        public override IEnumerable<DownloadClientItem> GetItems()
        {
            var queue = _proxy.GetQueue(Settings);
            foreach (var item in queue)
                item.DownloadClientInfo = DownloadClientItemClientInfo.FromDownloadClient(this, false);

            MaybeSweepEmptyDownloadDirectories(queue);
            return queue;
        }


        // Independent, throttled sweep of empty download shells — the gap the
        // per-item RemoveItem cleanup misses (restart / untracked / importFailed).
        // Throttle, single-flight and pruning all live in the shared sweeper.
        private void MaybeSweepEmptyDownloadDirectories(IEnumerable<DownloadClientItem> queue)
        {
            EmptyDownloadDirectorySweeper.MaybePruneForRoot(
                Settings.DownloadPath,
                queue.Where(i => !i.OutputPath.IsEmpty).Select(i => i.OutputPath.FullPath),
                DateTime.UtcNow,
                _logger);
        }

        public override void RemoveItem(DownloadClientItem item, bool deleteData)
        {
            if (deleteData)
            {
                // DeleteItemData removes the album folder only; the artist folder we
                // created above it is left behind, so sweep parents to the root.
                DeleteItemData(item);
                if (!item.OutputPath.IsEmpty)
                    TryRemoveEmptyParentFolders(item.OutputPath.FullPath, Settings.DownloadPath, _logger);
            }

            _proxy.RemoveFromQueue(item.DownloadId, Settings);
        }

        // Canonical implementation lives in Core/Utilities/DownloadFolderCleanup —
        // kept here as a shim so existing call sites read unchanged.
        internal static void TryRemoveEmptyParentFolders(string startedAt, string downloadRoot, Logger logger)
            => DownloadFolderCleanup.TryRemoveEmptyParentFolders(startedAt, downloadRoot, "Tidal", logger);

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
            // Auth lives on the indexer; here we verify the configured download path is usable
            // so misconfigured paths (typos, missing volumes, bad permissions) surface up-front
            // instead of mid-download.
            ValidationFailure folderFailure = TestFolder(Settings.DownloadPath, "DownloadPath");
            if (folderFailure != null)
                failures.Add(folderFailure);
        }
    }
}
