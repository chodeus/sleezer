using NzbDrone.Core.Download;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;

[Flags]
public enum TransferStates
{
    None = 0,
    Queued = 2,
    Initializing = 4,
    InProgress = 8,
    Completed = 16,
    Succeeded = 32,
    Cancelled = 64,
    TimedOut = 128,
    Errored = 256,
    Rejected = 512,
    Aborted = 1024,
    Locally = 2048,
    Remotely = 4096,
}

public class SlskdFileState(SlskdDownloadFile file)
{
    public SlskdDownloadFile File { get; private set; } = file;
    public int RetryCount { get; private set; }
    private bool _retried = false;
    public int MaxRetryCount { get; private set; } = 1;
    public string State => File.State;
    public string PreviousState { get; private set; } = "Requested";

    // Watchdog tracking: when this file first entered a Queued state, last time
    // bytesTransferred moved, and what that value was. Used by SlskdWatchdog to
    // decide whether to cancel a stuck file.
    public DateTime? FirstQueuedAt { get; private set; }
    public DateTime? LastBytesAdvancedAt { get; private set; }
    public long LastBytesTransferred { get; private set; }

    public bool WatchdogCancelled { get; private set; }

    public void MarkWatchdogCancelled() => WatchdogCancelled = true;

    public DownloadItemStatus GetStatus()
    {
        DownloadItemStatus status = GetStatus(State);
        if ((status == DownloadItemStatus.Failed && RetryCount < MaxRetryCount) || _retried)
            return DownloadItemStatus.Warning;
        return status;
    }

    public static DownloadItemStatus GetStatus(string stateStr)
    {
        if (Enum.TryParse<TransferStates>(stateStr, ignoreCase: true, out TransferStates state))
            return GetStatus(state);
        return DownloadItemStatus.Queued;
    }

    public static DownloadItemStatus GetStatus(TransferStates state) => state switch
    {
        _ when state.HasFlag(TransferStates.Completed) && state.HasFlag(TransferStates.Succeeded) => DownloadItemStatus.Completed,
        _ when state.HasFlag(TransferStates.Completed) => DownloadItemStatus.Failed,
        _ when state.HasFlag(TransferStates.Rejected) => DownloadItemStatus.Failed,
        _ when state.HasFlag(TransferStates.TimedOut) => DownloadItemStatus.Failed,
        _ when state.HasFlag(TransferStates.Errored) => DownloadItemStatus.Failed,
        _ when state.HasFlag(TransferStates.Cancelled) => DownloadItemStatus.Failed,
        _ when state.HasFlag(TransferStates.Aborted) => DownloadItemStatus.Failed,
        _ when state.HasFlag(TransferStates.InProgress) => DownloadItemStatus.Downloading,
        _ when state.HasFlag(TransferStates.Initializing) => DownloadItemStatus.Queued,
        _ when state.HasFlag(TransferStates.Queued) => DownloadItemStatus.Queued,
        _ => DownloadItemStatus.Queued,
    };

    public void UpdateFile(SlskdDownloadFile file)
    {
        if (!_retried)
            PreviousState = State;
        else if (File != null && GetStatus(file.State) == DownloadItemStatus.Failed)
            PreviousState = "Requested";
        File = file;
        _retried = false;

        UpdateWatchdogState(file);
    }

    private void UpdateWatchdogState(SlskdDownloadFile file)
    {
        DownloadItemStatus status = GetStatus(file.State);
        DateTime now = DateTime.UtcNow;

        // Record first-queued-at the first time we see this file in a queued state.
        if (FirstQueuedAt == null && status == DownloadItemStatus.Queued)
            FirstQueuedAt = now;

        // Reset the queue timer once the file has actually started or finished;
        // a re-enqueue after a failure treats the new queue wait as fresh.
        if (status != DownloadItemStatus.Queued)
            FirstQueuedAt = null;

        // Track bytes-transferred progress for stall detection.
        if (file.BytesTransferred > LastBytesTransferred)
        {
            LastBytesTransferred = file.BytesTransferred;
            LastBytesAdvancedAt = now;
        }
        else if (LastBytesAdvancedAt == null && status == DownloadItemStatus.Downloading)
        {
            // First time we see this file in progress, seed the stall baseline.
            LastBytesAdvancedAt = now;
            LastBytesTransferred = file.BytesTransferred;
        }
    }

    public void UpdateMaxRetryCount(int maxRetryCount) => MaxRetryCount = maxRetryCount;

    public void IncrementAttempt()
    {
        _retried = true;
        RetryCount++;
    }
}
