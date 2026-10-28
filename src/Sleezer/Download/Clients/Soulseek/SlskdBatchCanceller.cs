using System.Net;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;

/// <summary>Cancels and removes a peer's transfers by enqueue batch, the id slskd echoes on every transfer.</summary>
public static class SlskdBatchCanceller
{
    // As RemoveItem has always waited: slskd removes a transfer only once the cancel has ended it.
    private static readonly TimeSpan CancelSettle = TimeSpan.FromSeconds(1);

    /// <summary>Lists the peer's transfers and cancels those in <paramref name="batchIds"/>; returns how many slskd removed.</summary>
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
            logger.Warn(ex, "Could not list {Username}'s transfers to cancel batch(es) {Batches}; the next poll retries", username, string.Join(", ", batchIds));
            return 0;
        }

        return await CancelFilesAsync(apiClient, settings, username, InBatches((transfers?.Directories ?? []).SelectMany(d => d.Files ?? []), batchIds, skipTransferIds), logger);
    }

    /// <summary>The transfers enqueued in one of <paramref name="batchIds"/>; a transfer without a batch id is never one.</summary>
    public static List<SlskdDownloadFile> InBatches(IEnumerable<SlskdDownloadFile> files, IReadOnlySet<string> batchIds, IReadOnlySet<string>? skipTransferIds) =>
        [.. files.Where(f => f.BatchId != null && batchIds.Contains(f.BatchId) && skipTransferIds?.Contains(f.Id) != true)];

    /// <summary>Cancels the live transfers, then removes them all; returns how many slskd removed.</summary>
    public static async Task<int> CancelFilesAsync(ISlskdApiClient apiClient, SlskdProviderSettings settings, string username, List<SlskdDownloadFile> files, Logger logger)
    {
        List<SlskdDownloadFile> live = [.. files.Where(f => SlskdFileState.GetStatus(f.State) != DownloadItemStatus.Completed)];
        foreach (SlskdDownloadFile file in live)
            await TryDeleteAsync(apiClient, settings, username, file, remove: false, logger);

        if (live.Count > 0)
            await Task.Delay(CancelSettle);

        int removed = 0;
        foreach (SlskdDownloadFile file in files)
        {
            if (await TryDeleteAsync(apiClient, settings, username, file, remove: true, logger))
                removed++;
        }

        return removed;
    }

    // slskd reports the peer's path, often with Windows separators.
    private static string LocalName(SlskdDownloadFile file) => Path.GetFileName(file.Filename.Replace('\\', '/'));

    // A transfer slskd already dropped answers 404; the rest of the batch must still go.
    private static async Task<bool> TryDeleteAsync(ISlskdApiClient apiClient, SlskdProviderSettings settings, string username, SlskdDownloadFile file, bool remove, Logger logger)
    {
        try
        {
            await apiClient.DeleteTransferAsync(settings, username, file.Id, remove);
            return true;
        }
        catch (HttpException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            logger.Debug("Transfer {TransferId} ({Filename}) was already gone", file.Id, LocalName(file));
            return false;
        }
        catch (Exception ex)
        {
            logger.Warn(ex, "Could not {Action} transfer {TransferId} ({Filename}); the next poll retries", remove ? "remove" : "cancel", file.Id, LocalName(file));
            return false;
        }
    }
}
