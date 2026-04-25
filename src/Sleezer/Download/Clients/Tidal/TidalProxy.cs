using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Download.Clients.Tidal.Queue;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Core.PostProcessing;
using TidalSharp.Data;

namespace NzbDrone.Core.Download.Clients.Tidal
{
    public interface ITidalProxy
    {
        List<DownloadClientItem> GetQueue(TidalSettings settings);
        Task<string> Download(RemoteAlbum remoteAlbum, TidalSettings settings);
        void RemoveFromQueue(string downloadId, TidalSettings settings);
    }

    public class TidalProxy : ITidalProxy
    {
        private readonly ICached<DateTime?> _startTimeCache;
        private readonly DownloadTaskQueue _taskQueue;

        public TidalProxy(
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

        public List<DownloadClientItem> GetQueue(TidalSettings settings)
        {
            _taskQueue.SetSettings(settings);

            var listing = _taskQueue.GetQueueListing();
            var completed = listing.Where(x => x.Status == DownloadItemStatus.Completed);
            var queue = listing.Where(x => x.Status == DownloadItemStatus.Queued);
            var current = listing.Where(x => x.Status == DownloadItemStatus.Downloading);

            return completed.Concat(current).Concat(queue).Where(x => x != null).Select(ToDownloadClientItem).ToList();
        }

        public void RemoveFromQueue(string downloadId, TidalSettings settings)
        {
            _taskQueue.SetSettings(settings);
            var item = _taskQueue.GetQueueListing().FirstOrDefault(a => a.ID == downloadId);
            if (item != null)
                _taskQueue.RemoveItem(item);
        }

        public async Task<string> Download(RemoteAlbum remoteAlbum, TidalSettings settings)
        {
            _taskQueue.SetSettings(settings);
            var downloadItem = await DownloadItem.From(remoteAlbum)
                ?? throw new InvalidOperationException($"Unable to parse Tidal URL from release: {remoteAlbum.Release.DownloadUrl}");
            await _taskQueue.QueueBackgroundWorkItemAsync(downloadItem);
            return downloadItem.ID;
        }

        private DownloadClientItem ToDownloadClientItem(DownloadItem x)
        {
            string format = x.Bitrate switch
            {
                AudioQuality.LOW => "AAC (M4A) 96kbps",
                AudioQuality.HIGH => "AAC (M4A) 320kbps",
                AudioQuality.LOSSLESS => "FLAC (M4A) Lossless",
                AudioQuality.HI_RES_LOSSLESS => "FLAC (M4A) 24bit Lossless",
                _ => "Unknown"
            };

            string title = $"{x.Artist} - {x.Title} [WEB] [{format}]";
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
                CanBeRemoved = true
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
