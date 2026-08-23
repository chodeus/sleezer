using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Music;
using NzbDrone.Plugin.Sleezer.Core.Download;
using NzbDrone.Plugin.Sleezer.Core.PostProcessing;
using NzbDrone.Plugin.Sleezer.Metadata.FFmpeg;

namespace NzbDrone.Core.Download.Clients.Deezer.Queue
{
    public class DownloadTaskQueue
    {
        // One at a time, where Tidal and Qobuz run three.
        private const int ConcurrentAlbums = 1;

        private readonly DownloadPump<DownloadItem> _pump;
        private readonly Logger _logger;
        private readonly PostProcessRunner _postProcess;
        private readonly IDiskProvider _diskProvider;

        private DeezerSettings? _settings;

        // 0 = rehydration not yet attempted, 1 = attempted.
        private int _rehydrated;

        public DownloadTaskQueue(
            int capacity,
            DeezerSettings? settings,
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
            _pump = new DownloadPump<DownloadItem>(capacity, ConcurrentAlbums, "Deezer", logger, RunItemAsync);
        }

        public void SetSettings(DeezerSettings settings)
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
            if (_settings == null)
                throw new InvalidOperationException("Deezer queue received an item before settings were populated");

            item.EnsureValidity();
            await item.DoDownload(_settings, _logger, token);

            if (item.Status == DownloadItemStatus.Completed && !await RunPostProcessAsync(item, token))
                item.Status = DownloadItemStatus.Failed;

            TryPersistCompletedItem(item);
        }

        private async Task<bool> RunPostProcessAsync(DownloadItem item, CancellationToken ct)
        {
            Album? album = item.RemoteAlbum?.Albums?.FirstOrDefault();
            var request = new PostProcessRequest(
                PostProcessClient.Deezer,
                item.ID,
                item.Title,
                item.DownloadFolder ?? string.Empty,
                nameof(NzbDrone.Core.Indexers.DeezerDownloadProtocol),
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
                _logger.Warn(ex, "Failed to persist Deezer download state for {Title}; this download will not survive a Lidarr restart.", item.Title);
            }
        }

        private void TryRehydrateFromDisk(DeezerSettings settings)
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
                        _logger.Debug(ex, "Skipping unreadable Deezer sidecar at {Path}", sidecarPath);
                        continue;
                    }

                    if (persisted == null || persisted.Status != DownloadItemStatus.Completed)
                        continue;

                    if (_pump.TryAddRecovered(DownloadItem.FromPersisted(persisted)))
                        count++;
                }

                if (count > 0)
                    _logger.Info("Rehydrated {Count} completed Deezer download(s) from disk.", count);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to scan Deezer download path for persisted state; starting with an empty queue.");
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
                _logger.Debug(ex, "Failed to delete Deezer sidecar in {Folder}", item.DownloadFolder);
            }
        }
    }
}
