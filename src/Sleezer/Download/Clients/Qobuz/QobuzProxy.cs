using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Download.Clients.Qobuz.Queue;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Core.PostProcessing;
using NzbDrone.Plugin.Sleezer.Qobuz;
using QobuzApiSharp.Exceptions;

namespace NzbDrone.Core.Download.Clients.Qobuz
{
    public interface IQobuzProxy
    {
        List<DownloadClientItem> GetQueue(QobuzSettings settings);
        Task<string> Download(RemoteAlbum remoteAlbum, QobuzSettings settings);
        void RemoveFromQueue(string downloadId, QobuzSettings settings);
    }

    public class QobuzProxy : IQobuzProxy
    {
        private readonly ICached<DateTime?> _startTimeCache;
        private readonly DownloadTaskQueue _taskQueue;

        public QobuzProxy(
            ICacheManager cacheManager,
            ICorruptionScanner corruptionScanner,
            ICorruptionFailureHandler corruptionFailureHandler,
            IPreImportTagger preImportTagger,
            IMetadataFactory metadataFactory,
            IDiskProvider diskProvider,
            Logger logger)
        {
            _startTimeCache = cacheManager.GetCache<DateTime?>(GetType(), "startTimes");
            _taskQueue = new(500, null, corruptionScanner, corruptionFailureHandler, preImportTagger, metadataFactory, diskProvider, logger);
            _taskQueue.StartQueueHandler();
        }

        public List<DownloadClientItem> GetQueue(QobuzSettings settings)
        {
            _taskQueue.SetSettings(settings);

            var listing = _taskQueue.GetQueueListing();
            var completed = listing.Where(x => x.Status == DownloadItemStatus.Completed);
            var queue = listing.Where(x => x.Status == DownloadItemStatus.Queued);
            var current = listing.Where(x => x.Status == DownloadItemStatus.Downloading);

            // Failed items have to be reported or Lidarr never runs its failed-download
            // handling — the queue keeps them, so hiding them here strands them.
            var failed = listing.Where(x => x.Status == DownloadItemStatus.Failed);

            return [.. completed.Concat(current).Concat(queue).Concat(failed).Where(x => x != null).Select(ToDownloadClientItem)];
        }

        public void RemoveFromQueue(string downloadId, QobuzSettings settings)
        {
            _taskQueue.SetSettings(settings);
            var item = _taskQueue.GetQueueListing().FirstOrDefault(a => a.ID == downloadId);
            if (item != null)
                _taskQueue.RemoveItem(item);
        }

        public async Task<string> Download(RemoteAlbum remoteAlbum, QobuzSettings settings)
        {
            _taskQueue.SetSettings(settings);

            DownloadItem? downloadItem;
            try
            {
                downloadItem = await DownloadItem.From(remoteAlbum);
            }
            catch (ApiErrorResponseException ex)
            {
                // Qobuz refused the album's data — most often it isn't licensed in the
                // account's country. Fail the grab as a ReleaseDownloadException so Lidarr
                // logs it and returns 409 rather than a raw 500.
                throw new ReleaseDownloadException(remoteAlbum.Release,
                    $"Qobuz could not provide data for '{remoteAlbum.Release.Title}' — it may not be licensed in {QobuzAPI.Instance?.CountryCode}.", ex);
            }
            catch (Exception ex)
            {
                throw new ReleaseDownloadException(remoteAlbum.Release,
                    $"Failed to prepare the Qobuz download for '{remoteAlbum.Release.Title}'.", ex);
            }

            if (downloadItem == null)
                throw new ReleaseDownloadException(remoteAlbum.Release,
                    $"Unable to parse a Qobuz album URL from release: {remoteAlbum.Release.DownloadUrl}");

            downloadItem.CaptureSettings(settings);
            await _taskQueue.QueueBackgroundWorkItemAsync(downloadItem);
            return downloadItem.ID;
        }

        private DownloadClientItem ToDownloadClientItem(DownloadItem x)
        {
            string format = x.Bitrate switch
            {
                AudioQuality.MP3320 => "MP3 320kbps",
                AudioQuality.FLACLossless => "FLAC Lossless",
                AudioQuality.FLACHiRes24Bit96kHz => "FLAC 24bit 96kHz",
                AudioQuality.FLACHiRes24Bit192Khz => "FLAC 24bit 192kHz",
                _ => "Unknown",
            };

            var title = $"{x.Artist} - {x.Title} [WEB] [{format}]";
            if (x.Explicit)
                title += " [Explicit]";

            var item = new DownloadClientItem
            {
                DownloadId = x.ID,
                Title = title,
                TotalSize = x.TotalSize,
                RemainingSize = x.TotalSize - x.DownloadedSize,
                RemainingTime = GetRemainingTime(x),
                Status = x.Status,
                CanMoveFiles = true,
                CanBeRemoved = true,
            };

            if (x.DownloadFolder.IsNotNullOrWhiteSpace())
                item.OutputPath = new OsPath(x.DownloadFolder);

            return item;
        }

        private TimeSpan? GetRemainingTime(DownloadItem x)
        {
            if (x.Status == DownloadItemStatus.Completed)
            {
                _startTimeCache.Remove(x.ID);
                return null;
            }

            if (x.Progress == 0)
                return null;

            var started = _startTimeCache.Find(x.ID);
            if (started == null)
            {
                started = DateTime.UtcNow;
                _startTimeCache.Set(x.ID, started);
                return null;
            }

            var elapsed = DateTime.UtcNow - started;
            var progress = Math.Min(x.Progress, 1);

            return TimeSpan.FromTicks((long)(elapsed.Value.Ticks * (1 - progress) / progress));
        }
    }
}
