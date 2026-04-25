using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;
using NzbDrone.Core.Validation;
using NzbDrone.Plugin.Sleezer.Deezer;

namespace NzbDrone.Core.Download.Clients.Deezer
{
    public class Deezer : DownloadClientBase<DeezerSettings>
    {
        private readonly IDeezerProxy _proxy;

        public Deezer(IDeezerProxy proxy,
                      IConfigService configService,
                      IDiskProvider diskProvider,
                      IRemotePathMappingService remotePathMappingService,
                      ILocalizationService localizationService,
                      Logger logger)
            : base(configService, diskProvider, remotePathMappingService, localizationService, logger)
        {
            _proxy = proxy;
        }

        public override string Protocol => nameof(DeezerDownloadProtocol);

        public override string Name => "Deezer";

        public override IEnumerable<DownloadClientItem> GetItems()
        {
            var queue = _proxy.GetQueue(Settings);

            foreach (var item in queue)
            {
                item.DownloadClientInfo = DownloadClientItemClientInfo.FromDownloadClient(this, false);
            }

            return queue;
        }

        public override void RemoveItem(DownloadClientItem item, bool deleteData)
        {
            if (deleteData)
            {
                // Lidarr's DeleteItemData removes the album folder we exposed
                // via OutputPath. It does NOT walk up and remove the now-empty
                // artist folder we created above it. Sweep parents back to the
                // configured download root so users don't end up with an
                // ever-growing tree of empty folders.
                DeleteItemData(item);
                if (!item.OutputPath.IsEmpty)
                    TryRemoveEmptyParentFolders(item.OutputPath.FullPath, Settings.DownloadPath, _logger);
            }

            _proxy.RemoveFromQueue(item.DownloadId, Settings);
        }

        // Walks upward from `startedAt` (which has already been removed) and
        // deletes each parent that's now empty, stopping at `downloadRoot` or
        // at the first non-empty parent. Defensive — any failure (permissions,
        // race with another writer) just stops the sweep, never throws.
        // Internal-static so DownloadItem's failed-download cleanup can reuse it.
        internal static void TryRemoveEmptyParentFolders(string startedAt, string downloadRoot, Logger logger)
        {
            try
            {
                string normalizedRoot = Path.GetFullPath(downloadRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string? current = Path.GetDirectoryName(Path.GetFullPath(startedAt));

                while (!string.IsNullOrEmpty(current))
                {
                    string normalizedCurrent = current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                    // Never delete at or above the configured download root.
                    if (normalizedCurrent.Length <= normalizedRoot.Length ||
                        !normalizedCurrent.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                        return;

                    if (!Directory.Exists(current))
                        return;

                    // Stop at the first parent with anything in it (files OR
                    // unrelated subfolders from another grab).
                    if (Directory.EnumerateFileSystemEntries(current).Any())
                        return;

                    try
                    {
                        Directory.Delete(current, recursive: false);
                        logger.Debug("Deezer: removed empty parent folder {Folder}", current);
                    }
                    catch (Exception ex)
                    {
                        logger.Debug(ex, "Deezer: could not remove empty parent {Folder}; stopping sweep", current);
                        return;
                    }

                    current = Path.GetDirectoryName(current);
                }
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "Deezer: empty-parent sweep aborted from {Start}", startedAt);
            }
        }

        public override Task<string> Download(RemoteAlbum remoteAlbum, IIndexer indexer)
        {
            return _proxy.Download(remoteAlbum, Settings);
        }

        public override DownloadClientInfo GetStatus()
        {
            return new DownloadClientInfo
            {
                IsLocalhost = true,
                OutputRootFolders = new() { new OsPath(Settings.DownloadPath) }
            };
        }

        protected override void Test(List<ValidationFailure> failures)
        {
            // ARL is checked at indexer-level. Here we only verify the configured download path
            // is usable so misconfigured paths surface up-front instead of mid-download.
            ValidationFailure folderFailure = TestFolder(Settings.DownloadPath, "DownloadPath");
            if (folderFailure != null)
                failures.Add(folderFailure);
        }
    }
}
