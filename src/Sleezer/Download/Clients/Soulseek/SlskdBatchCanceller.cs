using NLog;
using NzbDrone.Core.Download;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;

/// <summary>
/// Cancels and removes a peer's transfers by enqueue batch. slskd echoes the batch id on every
/// transfer, so it reaches the ones a removed grab's last poll never saw (a retry, a first poll).
/// </summary>
public static class SlskdBatchCanceller
{
    // As RemoveItem has always waited: slskd removes a transfer only once the cancel has ended it.
    private static readonly TimeSpan CancelSettle = TimeSpan.FromSeconds(1);

    /// <summary>Returns how many transfers it cancelled or removed; transfers in <paramref name="skipTransferIds"/> are the caller's.</summary>
    public static async Task<int> CancelAsync(
        ISlskdApiClient apiClient,
        SlskdProviderSettings settings,
        string username,
        IReadOnlySet<string> batchIds,
        IReadOnlySet<string>? skipTransferIds,
        Logger logger)
    {
        if (batchIds.Count == 0)
            return 0;

        SlskdUserTransfers? transfers;
        try
        {
            transfers = await apiClient.GetUserTransfersAsync(settings, username);
        }
        catch (Exception ex)
        {
            logger.Warn(ex, "Could not list {Username}'s transfers to cancel batch(es) {Batches}", username, string.Join(", ", batchIds));
            return 0;
        }

        List<SlskdDownloadFile> files = [.. (transfers?.Directories ?? [])
            .SelectMany(d => d.Files ?? [])
            .Where(f => f.BatchId != null && batchIds.Contains(f.BatchId) && skipTransferIds?.Contains(f.Id) != true)];

        List<SlskdDownloadFile> live = [.. files.Where(f => SlskdFileState.GetStatus(f.State) != DownloadItemStatus.Completed)];
        foreach (SlskdDownloadFile file in live)
            await TryDeleteAsync(apiClient, settings, username, file, remove: false, logger);

        if (live.Count > 0)
            await Task.Delay(CancelSettle);

        foreach (SlskdDownloadFile file in files)
            await TryDeleteAsync(apiClient, settings, username, file, remove: true, logger);

        return files.Count;
    }

    // A transfer slskd already dropped answers 404; the rest of the batch must still go.
    private static async Task TryDeleteAsync(ISlskdApiClient apiClient, SlskdProviderSettings settings, string username, SlskdDownloadFile file, bool remove, Logger logger)
    {
        try
        {
            await apiClient.DeleteTransferAsync(settings, username, file.Id, remove);
        }
        catch (Exception ex)
        {
            logger.Debug(ex, "Could not {Action} transfer {TransferId} ({Filename})", remove ? "remove" : "cancel", file.Id, Path.GetFileName(file.Filename));
        }
    }
}
