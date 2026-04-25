using NzbDrone.Core.Download;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;
using Xunit;

namespace Sleezer.Tests;

// Covers the retry-exhaustion escalation path. Before this fix, a Slskd transfer
// whose retries were exhausted on a dead peer could sit in DownloadItemStatus.Warning
// indefinitely — Lidarr never saw Failed, so it never re-searched for a different
// release / different peer. The RetriesExhausted flag plus the Completed/Downloading
// priority in GetStatus() makes the terminal-failed state both sticky and safe.
public class SlskdFileStateRetryTests
{
    private static SlskdDownloadFile NewFile(string state) => new(
        Id: "id",
        Username: "peer",
        Direction: "Download",
        Filename: "Artist/Album/01 - Track.flac",
        Size: 1000,
        StartOffset: 0,
        State: state,
        RequestedAt: DateTime.UtcNow,
        EnqueuedAt: DateTime.UtcNow,
        StartedAt: DateTime.UtcNow,
        BytesTransferred: 0,
        AverageSpeed: 0,
        BytesRemaining: 1000,
        ElapsedTime: TimeSpan.Zero,
        PercentComplete: 0,
        RemainingTime: TimeSpan.Zero,
        EndedAt: null);

    [Fact]
    public void in_flight_retry_reports_Warning_until_budget_exhausted()
    {
        SlskdFileState fs = new(NewFile("Completed, Errored"));
        fs.UpdateMaxRetryCount(3);

        // Initial failure with budget remaining.
        Assert.Equal(DownloadItemStatus.Warning, fs.GetStatus());

        // Two retries used, one left — still Warning.
        fs.IncrementAttempt();
        fs.UpdateFile(NewFile("Completed, Errored"));
        Assert.Equal(DownloadItemStatus.Warning, fs.GetStatus());

        fs.IncrementAttempt();
        fs.UpdateFile(NewFile("Completed, Errored"));
        Assert.Equal(DownloadItemStatus.Warning, fs.GetStatus());
    }

    [Fact]
    public void IncrementAttempt_alone_does_not_set_RetriesExhausted()
    {
        // Only the retry handler sets the flag — it does so explicitly after
        // checking RetryCount >= MaxRetryCount. The model itself stays passive.
        SlskdFileState fs = new(NewFile("Completed, Errored"));
        fs.UpdateMaxRetryCount(1);

        fs.IncrementAttempt();
        Assert.False(fs.RetriesExhausted);
    }

    [Fact]
    public void MarkRetriesExhausted_promotes_status_to_Failed_for_terminal_states()
    {
        foreach (string state in new[] { "Completed, Errored", "Completed, Cancelled", "Completed, TimedOut", "Completed, Rejected" })
        {
            SlskdFileState fs = new(NewFile(state));
            fs.UpdateMaxRetryCount(1);
            fs.IncrementAttempt();
            fs.MarkRetriesExhausted();

            Assert.Equal(DownloadItemStatus.Failed, fs.GetStatus());
        }
    }

    [Fact]
    public void MarkRetriesExhausted_promotes_stuck_remote_queue_to_Failed()
    {
        // The Mode-B regression: the dead-peer case where slskd reports the file
        // as "Queued, Remotely" forever after a retry. Without the flag, GetStatus
        // would return Queued and the resolver would never see failure.
        SlskdFileState fs = new(NewFile("Queued, Remotely"));
        fs.UpdateMaxRetryCount(1);
        fs.IncrementAttempt();
        fs.MarkRetriesExhausted();

        Assert.Equal(DownloadItemStatus.Failed, fs.GetStatus());
    }

    [Fact]
    public void Completed_state_wins_over_RetriesExhausted()
    {
        // Corruption-path safety: corrupt files reach post-processing in
        // "Completed, Succeeded". If the corruption handler ever marked them
        // exhausted, GetStatus must still return Completed so the resolver
        // doesn't double-fire DownloadFailedEvent alongside the corruption
        // handler's own explicit publish.
        SlskdFileState fs = new(NewFile("Completed, Succeeded"));
        fs.UpdateMaxRetryCount(1);
        fs.IncrementAttempt();
        fs.MarkRetriesExhausted();

        Assert.Equal(DownloadItemStatus.Completed, fs.GetStatus());
    }

    [Fact]
    public void InProgress_state_wins_over_RetriesExhausted()
    {
        // In-flight retry safety: if the final retry is actively transferring
        // bytes, we must not flip it to Failed. The user would lose a download
        // that was on its way to succeeding.
        SlskdFileState fs = new(NewFile("InProgress"));
        fs.UpdateMaxRetryCount(1);
        fs.IncrementAttempt();
        fs.MarkRetriesExhausted();

        Assert.Equal(DownloadItemStatus.Downloading, fs.GetStatus());
    }

    [Fact]
    public void RetriesExhausted_is_sticky_across_state_transitions()
    {
        // Once set, the flag never clears. A file that briefly bounces back to
        // Queued during a slskd quirk must stay terminally failed.
        SlskdFileState fs = new(NewFile("Completed, Errored"));
        fs.UpdateMaxRetryCount(1);
        fs.IncrementAttempt();
        fs.MarkRetriesExhausted();
        Assert.Equal(DownloadItemStatus.Failed, fs.GetStatus());

        fs.UpdateFile(NewFile("Queued, Remotely"));
        Assert.Equal(DownloadItemStatus.Failed, fs.GetStatus());

        fs.UpdateFile(NewFile("Completed, Errored"));
        Assert.Equal(DownloadItemStatus.Failed, fs.GetStatus());
    }
}
