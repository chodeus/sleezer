using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Music;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.ThingiProvider;
using System.IO.Abstractions;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using NzbDrone.Plugin.Sleezer.Notifications.Queue;

namespace NzbDrone.Plugin.Sleezer.Notifications.QueueCleaner
{
    public class QueueCleaner : NotificationBase<QueueCleanerSettings>
    {
        private readonly Logger _logger;
        private readonly IDiskProvider _diskProvider;
        private readonly ICompletedDownloadService _completedDownloadService;
        private readonly IHistoryService _historyService;
        private readonly IEventAggregator _eventAggregator;
        private readonly INamingConfigService _namingConfig;
        private readonly IIndexerFactory _indexerFactory;
        private readonly IProvideDownloadClient _downloadClientProvider;
        private readonly IBlocklistRepository _blocklistRepository;
        private readonly List<IBlocklistForProtocol> _protocolBlocklists;

        public override string Name => "Queue Cleaner";

        public override string Link => "";

        public override ProviderMessage Message => new("Queue Cleaner automatically processes items that failed to import. It can rename, blocklist, or remove items based on your settings.", ProviderMessageType.Info);

        public QueueCleaner(IDiskProvider diskProvider, IHistoryService historyService, INamingConfigService namingConfig, IEventAggregator eventAggregator, IIndexerFactory indexerFactory, ICompletedDownloadService completedDownloadService,
            IProvideDownloadClient downloadClientProvider, IBlocklistRepository blocklistRepository, IEnumerable<IBlocklistForProtocol> protocolBlocklists, Logger logger)
        {
            _downloadClientProvider = downloadClientProvider;
            _blocklistRepository = blocklistRepository;
            _protocolBlocklists = [.. protocolBlocklists];
            _logger = logger;
            _indexerFactory = indexerFactory;
            _diskProvider = diskProvider;
            _eventAggregator = eventAggregator;
            _completedDownloadService = completedDownloadService;
            _historyService = historyService;
            _namingConfig = namingConfig;
        }

        public IFileInfo[] GetAudioFiles(string path, bool allDirectories = true)
        {
            if (!_diskProvider.FolderExists(path))
            {
                _logger.Debug("Cannot get audio files: Directory does not exist: {0}", path);
                return [];
            }

            List<IFileInfo> filesOnDisk = _diskProvider.GetFileInfos(path, allDirectories);
            return filesOnDisk.Where(file => MediaFileExtensions.Extensions.Contains(file.Extension)).ToArray();
        }

        internal void CleanImport(AlbumImportIncompleteEvent message)
        {
            TrackedDownload trackedDownload = message.TrackedDownload;

            if (!trackedDownload.IsTrackable || trackedDownload.State != TrackedDownloadState.ImportFailed || !trackedDownload.DownloadItem.CanMoveFiles)
                return;

            if (Settings.Indexers?.Any() == true && !Settings.Indexers.Contains(trackedDownload.Indexer))
            {
                _logger.Debug($"Skipping queue cleaning for download '{trackedDownload.DownloadItem.Title}'. Indexer '{trackedDownload.Indexer}' is not in list");
                return;
            }

            if (Settings.SkipReleaseSources != (int)SkipReleaseSourceOptions.Disabled)
            {
                ReleaseSourceType releaseSource = trackedDownload.RemoteAlbum?.ReleaseSource ?? ReleaseSourceType.Unknown;

                if (releaseSource == ReleaseSourceType.Unknown)
                {
                    List<EntityHistory> grabbedItems = [.. _historyService.Find(trackedDownload.DownloadItem.DownloadId, EntityHistoryEventType.Grabbed)];
                    if (grabbedItems.Count > 0)
                    {
                        EntityHistory historyItem = grabbedItems[^1];
                        _ = Enum.TryParse(historyItem.Data.GetValueOrDefault(EntityHistory.RELEASE_SOURCE, nameof(ReleaseSourceType.Unknown)), out releaseSource);
                    }
                }

                _logger.Trace($"Release source for download '{trackedDownload.DownloadItem.Title}': {releaseSource}, Skip threshold: {(SkipReleaseSourceOptions)Settings.SkipReleaseSources}");

                if ((int)releaseSource >= Settings.SkipReleaseSources && releaseSource != ReleaseSourceType.Unknown)
                {
                    _logger.Info($"Skipping queue cleaning for download '{trackedDownload.DownloadItem.Title}'. Release source '{releaseSource}' is at or above skip threshold '{(SkipReleaseSourceOptions)Settings.SkipReleaseSources}'.");
                    return;
                }
            }

            if (ImportRejections.ShouldClean((ImportCleaningOptions)Settings.ImportCleaningOption, trackedDownload.StatusMessages.SelectMany(sm => sm.Messages)))
                HandleFailure(trackedDownload);
        }

        private void HandleFailure(TrackedDownload trackedDownload)
        {
            if (Settings.RenameOption != (int)RenameOptions.DoNotRename && Rename(trackedDownload))
            {
                Retry(trackedDownload);
                return;
            }

            switch ((BlocklistOptions)Settings.BlocklistOption)
            {
                // Removed first, so Lidarr's own removal on the failed event below is skipped.
                case BlocklistOptions.RemoveAndBlocklist:
                    Remove(trackedDownload);
                    Blocklist(trackedDownload);
                    break;
                case BlocklistOptions.BlocklistOnly:
                    BlocklistOnly(trackedDownload);
                    break;
                case BlocklistOptions.RemoveOnly:
                    Remove(trackedDownload);
                    break;
            }
        }

        private void Remove(TrackedDownload item) => RemoveThroughClient(item, _downloadClientProvider, _logger);

        // Through the download client, which deletes only the download's own files, whatever its "Remove Completed" says.
        internal static void RemoveThroughClient(TrackedDownload item, IProvideDownloadClient downloadClientProvider, Logger logger)
        {
            if (!item.DownloadItem.CanBeRemoved || item.DownloadItem.Removed)
                return;

            try
            {
                downloadClientProvider.Get(item.DownloadClient).RemoveItem(item.DownloadItem, true);
                item.DownloadItem.Removed = true;
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "QueueCleaner: could not remove {Title} from its download client", item.DownloadItem.Title);
            }
        }

        private bool Rename(TrackedDownload item)
        {
            if (!item.DownloadItem.CanMoveFiles)
            {
                _logger.Debug("Skipping rename: Download item cannot be moved.");
                return false;
            }

            string downloadPath = item.DownloadItem.OutputPath.FullPath;
            if (!_diskProvider.FolderExists(downloadPath))
            {
                _logger.Debug("Skipping rename: Download directory no longer exists: {0}", downloadPath);
                return false;
            }

            if (item.RemoteAlbum == null)
            {
                _logger.Debug("Skipping rename: no album for {0}", item.DownloadItem.Title);
                return false;
            }

            List<IFileInfo> filesOnDisk = [.. _diskProvider.GetFileInfos(downloadPath, true)];
            HashSet<string> audioExtensions = new(MediaFileExtensions.Extensions, StringComparer.OrdinalIgnoreCase);
            ReleaseFormatter releaseFormatter = new(item.RemoteAlbum.Release, item.RemoteAlbum.Artist, _namingConfig.GetConfig());
            string? albumTitle = item.RemoteAlbum.Albums?.FirstOrDefault()?.Title;

            // Every audio file: not Any, which stops at the first one renamed.
            bool renamed = false;
            foreach (IFileInfo file in filesOnDisk.Where(f => audioExtensions.Contains(Path.GetExtension(f.FullName))))
                renamed |= TryRenameFile(file, releaseFormatter, albumTitle, filesOnDisk, item.DownloadItem.DownloadId);

            return renamed;
        }

        private bool TryRenameFile(IFileInfo file, ReleaseFormatter releaseFormatter, string? albumTitle, List<IFileInfo> filesOnDisk, string downloadId)
        {
            string filePath = file.FullName;
            string? directory = Path.GetDirectoryName(filePath);
            if (directory == null)
            {
                _logger.Warn($"Unable to determine directory for file: {filePath}");
                return false;
            }

            try
            {
                TagLib.File fileTags = TagLib.File.Create(filePath);
                string artistName = fileTags.Tag.FirstAlbumArtist ?? fileTags.Tag.FirstPerformer ?? "UnknownArtist";
                string title = fileTags.Tag.Title ?? Path.GetFileNameWithoutExtension(filePath);
                string trackNumber = fileTags.Tag.Track.ToString("D2");

                string newFileNameWithoutExtension = releaseFormatter.BuildTrackFilename(null, new Track
                {
                    Title = title,
                    TrackNumber = trackNumber,
                    Artist = new Artist { Name = artistName }
                }, new Album { Title = albumTitle ?? title });

                string newFileName = $"{newFileNameWithoutExtension}{Path.GetExtension(filePath)}";
                string newFilePath = Path.Combine(directory, newFileName);

                if (!newFilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase) && MoveFile(filePath, newFilePath))
                {
                    // A download client that tracks its files by name (Soulseek) must learn the new one, or removal leaves it.
                    _eventAggregator.PublishEvent(new DownloadFileRenamedEvent(downloadId, filePath, newFilePath));
                    string oldBaseName = Path.GetFileNameWithoutExtension(filePath);
                    filesOnDisk.Where(f => !Path.GetExtension(f.FullName).Equals(Path.GetExtension(filePath), StringComparison.OrdinalIgnoreCase) &&
                                          Path.GetFileNameWithoutExtension(f.FullName).Equals(oldBaseName, StringComparison.OrdinalIgnoreCase)).ToList()
                              .ForEach(associatedFile => MoveFile(associatedFile.FullName, Path.Combine(directory, $"{newFileNameWithoutExtension}{Path.GetExtension(associatedFile.FullName)}")));
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to parse or rename file: {filePath}");
            }
            return false;
        }

        private bool MoveFile(string sourcePath, string destinationPath)
        {
            if (!_diskProvider.FileExists(sourcePath)) return false;
            try
            {
                _diskProvider.MoveFile(sourcePath, destinationPath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to rename file: {sourcePath}");
                return false;
            }
        }

        private void Blocklist(TrackedDownload item)
        {
            item.State = TrackedDownloadState.DownloadFailed;

            if (FailedEvent([.. _historyService.Find(item.DownloadItem.DownloadId, EntityHistoryEventType.Grabbed)], "Import failed: Item removed by Queue Cleaner.") is not { } failed)
            {
                _logger.Warn("QueueCleaner: no grab history for download {DownloadId}; skipping blocklist record", item.DownloadItem.DownloadId);
                return;
            }

            failed.TrackedDownload = item;
            failed.SkipRedownload = !Settings.RetryFindingRelease;
            _eventAggregator.PublishEvent(failed);
        }

        // The row only: the download isn't failed, so it stays queued for a manual import and nothing is searched again.
        private void BlocklistOnly(TrackedDownload item)
        {
            if (FailedEvent([.. _historyService.Find(item.DownloadItem.DownloadId, EntityHistoryEventType.Grabbed)], "Import failed: blocklisted by Queue Cleaner.") is not { } failed)
            {
                _logger.Warn("QueueCleaner: no grab history for download {DownloadId}; skipping blocklist record", item.DownloadItem.DownloadId);
                return;
            }

            if (BlocklistRow(failed, _protocolBlocklists) is { } row)
                _blocklistRepository.Insert(row);
        }

        // The failed-download event for a grab, from its grab history; null without one.
        internal static DownloadFailedEvent? FailedEvent(IReadOnlyList<EntityHistory> grabbedItems, string message)
        {
            if (grabbedItems.Count == 0)
                return null;

            EntityHistory historyItem = grabbedItems[^1];
            _ = Enum.TryParse(historyItem.Data.GetValueOrDefault(EntityHistory.RELEASE_SOURCE, nameof(ReleaseSourceType.Unknown)), out ReleaseSourceType releaseSource);

            return new DownloadFailedEvent
            {
                ArtistId = historyItem.ArtistId,
                AlbumIds = grabbedItems.Select(h => h.AlbumId).Distinct().ToList(),
                Quality = historyItem.Quality,
                SourceTitle = historyItem.SourceTitle,
                // EntityHistory.Data uses PascalCase keys ("DownloadClient" — see
                // EntityHistoryService.Handle(AlbumGrabbedEvent)) but the
                // EntityHistory.DOWNLOAD_CLIENT constant is lowercase. The dict is
                // case-sensitive so the constant silently returns null. Read the
                // actual stored key.
                DownloadClient = new Dictionary<string, string>(historyItem.Data, StringComparer.OrdinalIgnoreCase).GetValueOrDefault("DownloadClient"),
                DownloadId = historyItem.DownloadId,
                Message = message,
                Data = historyItem.Data,
                ReleaseSource = releaseSource
            };
        }

        // The row Lidarr's BlocklistService writes for a failed download (BlocklistService.Handle(DownloadFailedEvent)).
        internal static Blocklist? BlocklistRow(DownloadFailedEvent failed, IEnumerable<IBlocklistForProtocol> protocolBlocklists)
        {
            if (protocolBlocklists.FirstOrDefault(x => x.Protocol == failed.Data.GetValueOrDefault("protocol")) is not { } protocolBlocklist)
                return null;

            Blocklist row = protocolBlocklist.GetBlocklist(failed);
            if (Enum.TryParse(failed.Data.GetValueOrDefault("indexerFlags"), true, out IndexerFlags flags))
                row.IndexerFlags = flags;
            return row;
        }

        private void Retry(TrackedDownload item)
        {
            item.State = TrackedDownloadState.ImportPending;
            _completedDownloadService.Import(item);
        }

        public override void OnImportFailure(AlbumDownloadMessage message) => base.OnImportFailure(message);

        public override ValidationResult Test()
        {
            ValidationResult result = new();
            IEnumerable<string> validProviders = _indexerFactory.GetAvailableProviders().Select(x => x.Name);

            if (Settings.Indexers?.Any() == true)
            {
                List<string> invalidProviders = Settings.Indexers
                    .Where(indexer => !validProviders.Contains(indexer, StringComparer.OrdinalIgnoreCase))
                    .ToList();

                if (invalidProviders.Count != 0)
                {
                    string errorMessage = $"The following indexers are not valid or available: {string.Join(", ", invalidProviders)}";
                    result.Errors.Add(new ValidationFailure(nameof(Settings.Indexers), errorMessage, Settings.Indexers));
                }
            }

            return result;
        }
    }
}