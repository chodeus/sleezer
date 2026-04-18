using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;

public interface ISlskdCorruptionHandler
{
    Task HandleCorruptDownloadAsync(
        SlskdDownloadItem item,
        IReadOnlyList<CorruptionStrike> strikes,
        string? completedFolderPath,
        SlskdProviderSettings settings,
        CancellationToken ct);
}

public record CorruptionStrike(SlskdFileState? FileState, string? Reason);

public class SlskdCorruptionHandler : ISlskdCorruptionHandler
{
    private readonly IHistoryService _historyService;
    private readonly IEventAggregator _eventAggregator;
    private readonly IDiskProvider _diskProvider;
    private readonly ISlskdCorruptUserTracker _userTracker;
    private readonly Logger _logger;

    public SlskdCorruptionHandler(
        IHistoryService historyService,
        IEventAggregator eventAggregator,
        IDiskProvider diskProvider,
        ISlskdCorruptUserTracker userTracker,
        Logger logger)
    {
        _historyService = historyService;
        _eventAggregator = eventAggregator;
        _diskProvider = diskProvider;
        _userTracker = userTracker;
        _logger = logger;
    }

    public Task HandleCorruptDownloadAsync(
        SlskdDownloadItem item,
        IReadOnlyList<CorruptionStrike> strikes,
        string? completedFolderPath,
        SlskdProviderSettings settings,
        CancellationToken ct)
    {
        if (strikes.Count == 0)
            return Task.CompletedTask;

        try
        {
            // 1. Short-circuit the in-flight retry loop for every corrupt file.
            //    SlskdRetryHandler.OnFileStateChanged bails when MaxRetryCount <= RetryCount,
            //    so zeroing the budget prevents the re-enqueue path from firing even if a
            //    later state transition comes through.
            foreach (CorruptionStrike strike in strikes)
            {
                strike.FileState?.UpdateMaxRetryCount(0);
                string fn = strike.FileState != null ? Path.GetFileName(strike.FileState.File.Filename) : "(unmapped)";
                _logger.Info($"Corruption strike: {fn} | reason: {strike.Reason ?? "(unknown)"}");
            }

            // 2. Delete the whole slskd download folder from disk.
            //    User directive: one corrupt file poisons the album; delete everything
            //    so Lidarr starts from a clean slate when it re-grabs a different release.
            if (!string.IsNullOrEmpty(completedFolderPath) && _diskProvider.FolderExists(completedFolderPath))
            {
                try
                {
                    _diskProvider.DeleteFolder(completedFolderPath, recursive: true);
                    _logger.Info($"Deleted corrupt download folder: {completedFolderPath}");
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, $"Failed to delete corrupt download folder: {completedFolderPath}");
                }
            }

            // 3. Record per-user strikes for optional user ban.
            if (!string.IsNullOrEmpty(item.Username) && settings.BanUserAfterCorruptCount > 0)
            {
                _userTracker.RecordStrike(item.Username, strikes.Count, settings.BanUserAfterCorruptCount);
            }

            // 4. Find grab history and publish DownloadFailedEvent so Lidarr
            //    blocklists the release + triggers a fresh AlbumSearch.
            List<EntityHistory> grabbedItems = _historyService.Find(item.ID, EntityHistoryEventType.Grabbed);
            if (grabbedItems.Count == 0)
            {
                _logger.Warn($"Corruption: no grab history for download {item.ID}; cannot publish DownloadFailedEvent. File(s) deleted; Lidarr will rediscover empty slot on next search.");
                return Task.CompletedTask;
            }

            EntityHistory historyItem = grabbedItems[^1];

            _ = Enum.TryParse(historyItem.Data.GetValueOrDefault(EntityHistory.RELEASE_SOURCE, nameof(ReleaseSourceType.Unknown)),
                              out ReleaseSourceType releaseSource);

            TrackedDownload tracked = new()
            {
                DownloadItem = new DownloadClientItem
                {
                    DownloadId = item.ID,
                    Title = item.ReleaseInfo?.Title ?? string.Empty
                },
                State = TrackedDownloadState.DownloadFailed,
                Protocol = nameof(NzbDrone.Core.Indexers.SoulseekDownloadProtocol),
                Indexer = historyItem.Data.GetValueOrDefault("indexer") ?? string.Empty
            };

            DownloadFailedEvent evt = new()
            {
                ArtistId = historyItem.ArtistId,
                AlbumIds = grabbedItems.Select(h => h.AlbumId).Distinct().ToList(),
                Quality = historyItem.Quality,
                SourceTitle = historyItem.SourceTitle,
                DownloadClient = historyItem.Data.GetValueOrDefault(EntityHistory.DOWNLOAD_CLIENT),
                DownloadId = historyItem.DownloadId,
                Message = BuildFailureMessage(strikes),
                Data = historyItem.Data,
                TrackedDownload = tracked,
                SkipRedownload = false,  // we WANT Lidarr to search for a different release
                ReleaseSource = releaseSource
            };

            _eventAggregator.PublishEvent(evt);
            _logger.Info($"Corruption: published DownloadFailedEvent for {item.ID} ({strikes.Count} corrupt files) — Lidarr will blocklist and re-search");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"Corruption handler failed for {item.ID}");
        }

        return Task.CompletedTask;
    }

    private static string BuildFailureMessage(IReadOnlyList<CorruptionStrike> strikes)
    {
        string firstReason = strikes[0].Reason ?? "(unknown)";
        return strikes.Count == 1
            ? $"NzbDrone.Plugin.Sleezer: corrupt file detected — {firstReason}"
            : $"NzbDrone.Plugin.Sleezer: {strikes.Count} corrupt files detected; first: {firstReason}";
    }
}
