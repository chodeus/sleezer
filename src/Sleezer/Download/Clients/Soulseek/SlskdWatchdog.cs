using NLog;
using NzbDrone.Core.Download;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;

public interface ISlskdWatchdog
{
    /// <summary>
    /// Scan every tracked file for queue-position overruns, queue-wait overruns,
    /// or in-progress byte stalls. Cancel offending transfers in slskd so the
    /// file transitions to a failed state and Lidarr's retry/re-search pipeline
    /// takes over.
    /// </summary>
    Task InspectAsync(SlskdDownloadItem item, SlskdProviderSettings settings, CancellationToken ct);
}

public class SlskdWatchdog : ISlskdWatchdog
{
    private readonly ISlskdApiClient _apiClient;
    private readonly Logger _logger;

    public SlskdWatchdog(ISlskdApiClient apiClient, Logger logger)
    {
        _apiClient = apiClient;
        _logger = logger;
    }

    public async Task InspectAsync(SlskdDownloadItem item, SlskdProviderSettings settings, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(item.Username))
            return;

        // Short-circuit if all three checks are disabled.
        if (settings.MaxQueuePositionBeforeCancel <= 0
            && settings.MaxQueueWaitMinutes <= 0
            && settings.StallTimeoutMinutes <= 0)
            return;

        DateTime now = DateTime.UtcNow;

        foreach (SlskdFileState fileState in item.FileStates.Values)
        {
            if (fileState.WatchdogCancelled)
                continue;

            ct.ThrowIfCancellationRequested();

            DownloadItemStatus status = fileState.GetStatus();
            SlskdDownloadFile file = fileState.File;

            string? reason = EvaluateBailout(fileState, status, file, settings, now);
            if (reason == null)
                continue;

            await CancelAsync(item, fileState, settings, reason);
        }
    }

    private static string? EvaluateBailout(
        SlskdFileState fileState,
        DownloadItemStatus status,
        SlskdDownloadFile file,
        SlskdProviderSettings settings,
        DateTime now)
    {
        // Queue-position bailout: peer put us past the configured limit.
        if (status == DownloadItemStatus.Queued
            && settings.MaxQueuePositionBeforeCancel > 0
            && file.PlaceInQueue is int position
            && position > settings.MaxQueuePositionBeforeCancel)
        {
            return $"queue position {position} > threshold {settings.MaxQueuePositionBeforeCancel}";
        }

        // Queue-wait bailout: sitting in queue longer than the window.
        if (status == DownloadItemStatus.Queued
            && settings.MaxQueueWaitMinutes > 0
            && fileState.FirstQueuedAt is DateTime queuedAt)
        {
            TimeSpan waited = now - queuedAt;
            if (waited.TotalMinutes > settings.MaxQueueWaitMinutes)
                return $"queue wait {waited.TotalMinutes:F0}min > threshold {settings.MaxQueueWaitMinutes}min";
        }

        // Stall bailout: InProgress but bytes haven't moved.
        if (status == DownloadItemStatus.Downloading
            && settings.StallTimeoutMinutes > 0
            && fileState.LastBytesAdvancedAt is DateTime lastProgress)
        {
            TimeSpan stalled = now - lastProgress;
            if (stalled.TotalMinutes > settings.StallTimeoutMinutes)
                return $"no byte progress in {stalled.TotalMinutes:F0}min > threshold {settings.StallTimeoutMinutes}min";
        }

        return null;
    }

    private async Task CancelAsync(SlskdDownloadItem item, SlskdFileState fileState, SlskdProviderSettings settings, string reason)
    {
        string filename = Path.GetFileName(fileState.File.Filename);

        try
        {
            _logger.Info($"[watchdog] Cancelling {filename} from {item.Username}: {reason}");

            await _apiClient.DeleteTransferAsync(settings, item.Username!, fileState.File.Id);
            fileState.MarkWatchdogCancelled();
        }
        catch (Exception ex)
        {
            _logger.Warn(ex, $"[watchdog] Failed to cancel {filename} from {item.Username}");
        }
    }
}
