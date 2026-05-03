using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Plugin.Sleezer.Core.PostProcessing;

public record CorruptionStrike(string File, string? Reason);

public interface ICorruptionFailureHandler
{
    // Universal post-corruption workflow shared across every Sleezer download
    // client: wipe the download folder, locate the grab history for this
    // download, and publish DownloadFailedEvent so Lidarr blocklists the
    // release and searches for a different one. Client-specific pre-steps
    // (e.g. slskd retry-budget zeroing, peer strike tracking) live in the
    // calling handler and run before delegating here.
    Task HandleAsync(
        string downloadId,
        string releaseTitle,
        string folder,
        IReadOnlyList<CorruptionStrike> strikes,
        string protocolName,
        CancellationToken ct);
}

public class CorruptionFailureHandler : ICorruptionFailureHandler
{
    private readonly IHistoryService _historyService;
    private readonly IEventAggregator _eventAggregator;
    private readonly IDiskProvider _diskProvider;
    private readonly Logger _logger;

    public CorruptionFailureHandler(
        IHistoryService historyService,
        IEventAggregator eventAggregator,
        IDiskProvider diskProvider,
        Logger logger)
    {
        _historyService = historyService;
        _eventAggregator = eventAggregator;
        _diskProvider = diskProvider;
        _logger = logger;
    }

    public Task HandleAsync(
        string downloadId,
        string releaseTitle,
        string folder,
        IReadOnlyList<CorruptionStrike> strikes,
        string protocolName,
        CancellationToken ct)
    {
        if (strikes.Count == 0)
            return Task.CompletedTask;

        try
        {
            // 1. Wipe the whole download folder. One corrupt file poisons the
            //    album — leaving survivors behind would only tempt Lidarr to
            //    import a partial set. Deleting everything guarantees a clean
            //    slate when it re-grabs a different release.
            if (!string.IsNullOrEmpty(folder) && _diskProvider.FolderExists(folder))
            {
                try
                {
                    _diskProvider.DeleteFolder(folder, recursive: true);
                    _logger.Info("Deleted corrupt download folder: {Folder}", folder);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Failed to delete corrupt download folder: {Folder}", folder);
                }
            }

            // 2. Find grab history and publish DownloadFailedEvent so Lidarr
            //    blocklists this release and triggers a fresh AlbumSearch.
            List<EntityHistory> grabbedItems = _historyService.Find(downloadId, EntityHistoryEventType.Grabbed);
            if (grabbedItems.Count == 0)
            {
                _logger.Warn("Corruption: no grab history for download {DownloadId}; file(s) deleted, Lidarr will rediscover the empty slot on next search.", downloadId);
                return Task.CompletedTask;
            }

            EntityHistory historyItem = grabbedItems[^1];

            _ = Enum.TryParse(historyItem.Data.GetValueOrDefault(EntityHistory.RELEASE_SOURCE, nameof(ReleaseSourceType.Unknown)),
                              out ReleaseSourceType releaseSource);

            // Lidarr stores grab metadata in EntityHistory.Data using PascalCase
            // string literals ("DownloadClient", "DownloadClientName", "Indexer"
            // — see DownloadHistoryService.Handle(AlbumGrabbedEvent)) but the
            // EntityHistory.DOWNLOAD_CLIENT / .INDEXER constants are lowercase.
            // The dict is case-sensitive so reading via the constants silently
            // returns null, which used to leave DownloadClientInfo unpopulated
            // and NRE'd Lidarr's DownloadHistoryService.Handle(DownloadFailedEvent)
            // at line 230 (DownloadClientInfo.Type deref).
            string downloadClientType = historyItem.Data.GetValueOrDefault("DownloadClient") ?? string.Empty;
            string downloadClientName = historyItem.Data.GetValueOrDefault("DownloadClientName") ?? string.Empty;
            string indexer = historyItem.Data.GetValueOrDefault("Indexer") ?? string.Empty;

            TrackedDownload tracked = new()
            {
                DownloadItem = new DownloadClientItem
                {
                    DownloadId = downloadId,
                    Title = releaseTitle ?? string.Empty,
                    // Required by Lidarr's DownloadHistoryService — derefs
                    // DownloadClientInfo.Type / .Name without null-checks.
                    DownloadClientInfo = new DownloadClientItemClientInfo
                    {
                        Type = downloadClientType,
                        Name = downloadClientName,
                        Protocol = protocolName,
                    }
                },
                State = TrackedDownloadState.DownloadFailed,
                Protocol = protocolName,
                Indexer = indexer,
            };

            DownloadFailedEvent evt = new()
            {
                ArtistId = historyItem.ArtistId,
                AlbumIds = grabbedItems.Select(h => h.AlbumId).Distinct().ToList(),
                Quality = historyItem.Quality,
                SourceTitle = historyItem.SourceTitle,
                DownloadClient = downloadClientType,
                DownloadId = historyItem.DownloadId,
                Message = BuildFailureMessage(strikes),
                Data = historyItem.Data,
                TrackedDownload = tracked,
                SkipRedownload = false,
                ReleaseSource = releaseSource
            };

            _eventAggregator.PublishEvent(evt);
            _logger.Info("Corruption: published DownloadFailedEvent for {DownloadId} ({Count} corrupt files) — Lidarr will blocklist and re-search.",
                         downloadId, strikes.Count);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Corruption handler failed for {DownloadId}", downloadId);
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
