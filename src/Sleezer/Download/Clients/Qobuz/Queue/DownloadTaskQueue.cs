using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Music;
using NzbDrone.Plugin.Sleezer.Core.Download;
using NzbDrone.Plugin.Sleezer.Core.PostProcessing;
using NzbDrone.Plugin.Sleezer.Metadata.FFmpeg;

namespace NzbDrone.Core.Download.Clients.Qobuz.Queue
{
    public class DownloadTaskQueue
    {
        private const int ConcurrentAlbums = 3;

        private readonly DownloadPump<DownloadItem> _pump;
        private readonly Logger _logger;
        private readonly PostProcessRunner _postProcess;
        private readonly IDiskProvider _diskProvider;

        private QobuzSettings? _settings;

        // 0 = rehydration not yet attempted, 1 = attempted.
        private int _rehydrated;

        public DownloadTaskQueue(
            int capacity,
            QobuzSettings? settings,
            ICorruptionScanner corruptionScanner,
            ICorruptionFailureHandler corruptionFailureHandler,
            IPreImportTagger preImportTagger,
            IMetadataFactory metadataFactory,
            IDiskProvider diskProvider,
            Logger logger)
        {
            _settings = settings;
            _diskProvider = diskProvider;
            _logger = logger;
            _postProcess = new PostProcessRunner(corruptionScanner, corruptionFailureHandler, preImportTagger, metadataFactory, diskProvider, logger);
            _pump = new DownloadPump<DownloadItem>(capacity, ConcurrentAlbums, "Qobuz", logger, RunItemAsync);
        }

        public void SetSettings(QobuzSettings settings)
        {
            _settings = settings;
            if (Interlocked.CompareExchange(ref _rehydrated, 1, 0) == 0)
                TryRehydrateFromDisk(settings);
        }

        public void StartQueueHandler() => _pump.Start();

        public ValueTask QueueBackgroundWorkItemAsync(DownloadItem workItem) => _pump.EnqueueAsync(workItem);

        public DownloadItem[] GetQueueListing() => _pump.Listing();

        public CancellationToken GetTokenForItem(DownloadItem item) => _pump.TokenFor(item);

        public void RemoveItem(DownloadItem workItem)
        {
            if (workItem == null)
                return;

            _pump.Remove(workItem);
            TryDeleteSidecar(workItem);
        }

        private async Task RunItemAsync(DownloadItem item, CancellationToken token)
        {
            // The proxy always calls SetSettings before queueing, so this is genuinely
            // unexpected; the pump fails the item and keeps the loop running.
            if (_settings == null && item.Settings == null)
                throw new InvalidOperationException("Qobuz queue received an item before settings were populated");

            // The item's own snapshot wins: this queue is shared by every configured
            // Qobuz client, so the queue's current settings may belong to another one.
            await item.DoDownload(item.Settings ?? _settings!, _logger, token);

            if (item.Status == DownloadItemStatus.Completed && !await RunPostProcessAsync(item, token))
                item.Status = DownloadItemStatus.Failed;

            TryPersistCompletedItem(item);
        }

        private async Task<bool> RunPostProcessAsync(DownloadItem item, CancellationToken ct)
        {
            Album? album = item.RemoteAlbum?.Albums?.FirstOrDefault();
            var request = new PostProcessRequest(
                PostProcessClient.Qobuz,
                item.ID,
                item.Title,
                item.DownloadFolder ?? string.Empty,
                nameof(QobuzDownloadProtocol),
                album);

            return await _postProcess.RunAsync(request, ct);
        }

        // Only completed downloads are persisted: a failed item may have had files removed
        // by the corrupt-scan pass, so its on-disk state is not a valid import target.
        private void TryPersistCompletedItem(DownloadItem item)
        {
            if (item.Status != DownloadItemStatus.Completed
                || string.IsNullOrEmpty(item.DownloadFolder)
                || !_diskProvider.FolderExists(item.DownloadFolder))
                return;

            try
            {
                PersistedDownloadItem.CaptureFrom(item).WriteTo(item.DownloadFolder);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to persist Qobuz download state for {Title}; it will not survive a Lidarr restart.", item.Title);
            }
        }

        private void TryRehydrateFromDisk(QobuzSettings settings)
        {
            string? root = settings.DownloadPath;
            if (string.IsNullOrEmpty(root) || !_diskProvider.FolderExists(root))
                return;

            try
            {
                string[] sidecars = Directory.GetFiles(root, PersistedDownloadItem.SidecarFileName, SearchOption.AllDirectories);

                int count = 0;
                foreach (string sidecarPath in sidecars)
                {
                    PersistedDownloadItem? persisted;
                    try
                    {
                        persisted = PersistedDownloadItem.TryRead(sidecarPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "Skipping unreadable Qobuz sidecar at {Path}", sidecarPath);
                        continue;
                    }

                    if (persisted == null || persisted.Status != DownloadItemStatus.Completed)
                        continue;

                    if (_pump.TryAddRecovered(DownloadItem.FromPersisted(persisted)))
                        count++;
                }

                if (count > 0)
                    _logger.Info("Rehydrated {Count} completed Qobuz download(s) from disk.", count);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to scan the Qobuz download path for persisted state; starting with an empty queue.");
            }
        }

        private void TryDeleteSidecar(DownloadItem item)
        {
            if (string.IsNullOrEmpty(item.DownloadFolder))
                return;

            try
            {
                string path = PersistedDownloadItem.SidecarPath(item.DownloadFolder);
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                _logger.Trace(ex, "Failed to delete the Qobuz sidecar for {Title}", item.Title);
            }
        }
    }
}
