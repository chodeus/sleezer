using NzbDrone.Core.Download;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;

public static class SlskdStatusResolver
{
    public record DownloadStatus(
        DownloadItemStatus Status,
        string? Message,
        long TotalSize,
        long RemainingSize,
        TimeSpan? RemainingTime
    );

    public static DownloadStatus Resolve(SlskdDownloadItem item, TimeSpan? timeout, DateTime utcNow)
    {
        if (item.SlskdDownloadDirectory?.Files == null)
            return new(DownloadItemStatus.Queued, null, 0, 0, null);

        IReadOnlyList<SlskdDownloadFile> files = item.SlskdDownloadDirectory.Files;

        long totalSize = 0, remainingSize = 0, totalSpeed = 0;
        bool anyActive = false, anyIncomplete = false, allIncompleteRemoteQueued = true;
        DateTime lastActivity = DateTime.MinValue;

        foreach (SlskdDownloadFile f in files)
        {
            totalSize += f.Size;
            remainingSize += f.BytesRemaining;

            DownloadItemStatus fs = SlskdFileState.GetStatus(f.State);
            if (fs == DownloadItemStatus.Completed)
                continue;

            anyIncomplete = true;

            if (fs == DownloadItemStatus.Downloading)
            {
                anyActive = true;
                totalSpeed += (long)f.AverageSpeed;
            }
            else if (fs == DownloadItemStatus.Queued)
            {
                anyActive = true;
            }

            // Inactivity timestamp: max of enqueued / started / (started + elapsed)
            DateTime t3 = f.StartedAt + f.ElapsedTime;
            DateTime latest = f.EnqueuedAt > f.StartedAt ? f.EnqueuedAt : f.StartedAt;
            if (t3 > latest) latest = t3;
            if (latest > lastActivity) lastActivity = latest;

            // All-stuck check: short-circuit once one file is NOT stuck
            if (allIncompleteRemoteQueued)
            {
                bool stuckRemote = timeout.HasValue
                    && Enum.TryParse<TransferStates>(f.State, ignoreCase: true, out TransferStates ts)
                    && ts.HasFlag(TransferStates.Queued)
                    && ts.HasFlag(TransferStates.Remotely)
                    && (utcNow - f.EnqueuedAt) > timeout.Value;
                if (!stuckRemote)
                    allIncompleteRemoteQueued = false;
            }
        }

        bool allStuckInRemoteQueue = anyIncomplete && allIncompleteRemoteQueued;

        int totalFileCount = 0, failedCount = 0, completedCount = 0;
        bool anyWarning = false, anyPaused = false, anyDownloadingState = false;
        List<string> failedFileNames = [];

        foreach (SlskdFileState fs in item.FileStates.Values)
        {
            totalFileCount++;
            DownloadItemStatus s = fs.GetStatus();
            switch (s)
            {
                case DownloadItemStatus.Completed: completedCount++; break;
                case DownloadItemStatus.Failed:
                    failedCount++;
                    failedFileNames.Add(Path.GetFileName(fs.File.Filename));
                    break;
                case DownloadItemStatus.Warning: anyWarning = true; break;
                case DownloadItemStatus.Paused: anyPaused = true; break;
                case DownloadItemStatus.Downloading: anyDownloadingState = true; break;
            }
        }

        DownloadItemStatus status;
        string? message = null;

        if (allStuckInRemoteQueue && !anyActive)
        {
            status = DownloadItemStatus.Failed;
            message = "All files stuck in remote queue past timeout.";
        }
        else if (failedCount > 0)
        {
            // Any file that exhausted its retries fails the whole album — Lidarr
            // blocklists the release and re-searches with a different peer.
            // failedCount only includes files past MaxRetryCount; files still
            // retrying are counted as Warning via SlskdFileState.GetStatus().
            status = DownloadItemStatus.Failed;
            message = $"Downloading {failedCount} files failed: {string.Join(", ", failedFileNames)}";
        }
        else if (!anyActive && anyIncomplete)
        {
            status = timeout.HasValue && (utcNow - lastActivity) > timeout.Value * 2
                ? DownloadItemStatus.Failed
                : DownloadItemStatus.Queued;
        }
        else if (totalFileCount > 0 && completedCount == totalFileCount)
        {
            status = item.PostProcessTasks.Any(t => !t.IsCompleted)
                ? DownloadItemStatus.Downloading
                : DownloadItemStatus.Completed;
        }
        else if (anyPaused)
        {
            status = DownloadItemStatus.Paused;
        }
        else if (anyWarning)
        {
            status = DownloadItemStatus.Warning;
            message = "Some files failed. Retrying download...";
        }
        else if (anyDownloadingState)
        {
            status = DownloadItemStatus.Downloading;
        }
        else
        {
            status = DownloadItemStatus.Queued;
        }

        // Surface slskd queue-position info when idle in remote queue. Gives
        // the Lidarr UI a "queued at position X" summary instead of a blank.
        if (message == null && (status == DownloadItemStatus.Queued || status == DownloadItemStatus.Downloading))
        {
            message = BuildQueueMessage(files);
        }

        TimeSpan? remainingTime = totalSpeed > 0
            ? TimeSpan.FromSeconds(remainingSize / (double)totalSpeed)
            : null;

        return new(status, message, totalSize, remainingSize, remainingTime);
    }

    private static string? BuildQueueMessage(IReadOnlyList<SlskdDownloadFile> files)
    {
        int queuedCount = 0;
        int downloadingCount = 0;
        int completedCount = 0;
        List<int> positions = new();

        foreach (SlskdDownloadFile f in files)
        {
            DownloadItemStatus fs = SlskdFileState.GetStatus(f.State);
            switch (fs)
            {
                case DownloadItemStatus.Queued:
                    queuedCount++;
                    if (f.PlaceInQueue is int p && p > 0)
                        positions.Add(p);
                    break;
                case DownloadItemStatus.Downloading:
                    downloadingCount++;
                    break;
                case DownloadItemStatus.Completed:
                    completedCount++;
                    break;
            }
        }

        if (queuedCount == 0 && downloadingCount == 0)
            return null;

        List<string> parts = new();
        if (downloadingCount > 0)
            parts.Add($"{downloadingCount} downloading");
        if (queuedCount > 0)
        {
            string queued = $"{queuedCount} queued";
            if (positions.Count > 0)
            {
                int avg = (int)Math.Round(positions.Average());
                queued += $" (avg pos {avg})";
            }
            parts.Add(queued);
        }
        if (completedCount > 0)
            parts.Add($"{completedCount} done");

        return string.Join(", ", parts);
    }
}
