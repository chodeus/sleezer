using System.IO.Abstractions;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek;
using Xunit;

namespace Sleezer.Tests;

// Live 2026-10-28: a grab failed on peer rejections, sleezer re-enqueued the rejected files as Lidarr removed it,
// and the retries finished into the folder the next grab of the album used.
public class SlskdRemovedGrabTests
{
    private const string Track = @"@@peer\Artist\Album\01.flac";

    private sealed class FakeSlskd : ISlskdApiClient
    {
        public List<SlskdDownloadFile> Transfers { get; } = [];
        public List<(string Id, bool Remove)> Deletes { get; } = [];
        public List<string?> Enqueued { get; } = [];
        public string EnqueueBatchId { get; set; } = "retry-batch";
        public Action? DuringEnqueue { get; set; }
        public HashSet<string> FailingDeletes { get; } = [];
        public bool FailListing { get; set; }
        public TaskCompletionSource RemovedOne { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<SlskdEnqueueResult> EnqueueDownloadAsync(SlskdProviderSettings settings, string username, IEnumerable<(string Filename, long Size)> files, string? externalId = null, string? destination = null)
        {
            Enqueued.Add(destination);
            DuringEnqueue?.Invoke();
            return Task.FromResult(new SlskdEnqueueResult(EnqueueBatchId, [.. files.Select(f => f.Filename)], []));
        }

        public Task<SlskdUserTransfers?> GetUserTransfersAsync(SlskdProviderSettings settings, string username)
        {
            if (FailListing)
                throw new InvalidOperationException("slskd down");
            return Task.FromResult<SlskdUserTransfers?>(new SlskdUserTransfers
            {
                Username = username,
                Directories = [new SlskdDownloadDirectory(@"@@peer\Artist\Album", Transfers.Count, Transfers)]
            });
        }

        public Task DeleteTransferAsync(SlskdProviderSettings settings, string username, string fileId, bool remove = false)
        {
            if (FailingDeletes.Contains(fileId))
                throw new InvalidOperationException("404");
            Deletes.Add((fileId, remove));
            if (remove)
                RemovedOne.TrySetResult();
            return Task.CompletedTask;
        }

        public Task<List<SlskdUserTransfers>> GetAllTransfersAsync(SlskdProviderSettings settings, bool includeRemoved = false) =>
            Task.FromResult(new List<SlskdUserTransfers>());
        public Task<SlskdDownloadFile?> GetTransferAsync(SlskdProviderSettings settings, string username, string fileId) =>
            Task.FromResult<SlskdDownloadFile?>(null);
        public Task<int?> GetQueuePositionAsync(SlskdProviderSettings settings, string username, string fileId) =>
            Task.FromResult<int?>(null);
        public Task DeleteAllCompletedAsync(SlskdProviderSettings settings) => Task.CompletedTask;
        public Task<string?> GetDownloadPathAsync(SlskdProviderSettings settings) => Task.FromResult<string?>(null);
        public Task<SlskdDestinationConfig?> GetDestinationConfigAsync(SlskdProviderSettings settings) =>
            Task.FromResult<SlskdDestinationConfig?>(null);
        public Task<ValidationFailure?> TestConnectionAsync(SlskdProviderSettings settings) =>
            Task.FromResult<ValidationFailure?>(null);
        public Task<(List<SlskdEventRecord> Events, int TotalCount)> GetEventsAsync(SlskdProviderSettings settings, int offset, int limit) =>
            Task.FromResult((new List<SlskdEventRecord>(), 0));
    }

    private static SlskdDownloadFile Transfer(string id, string state, string? batchId) => new(
        Id: id,
        Username: "peer",
        Direction: "Download",
        Filename: Track,
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
        EndedAt: null,
        BatchId: batchId);

    private static SlskdDownloadItem Grab() => new(new ReleaseInfo
    {
        Source = $"[{{\"Filename\":{System.Text.Json.JsonSerializer.Serialize(Track)},\"Size\":1000}}]",
        Title = "t",
        DownloadUrl = "u"
    })
    {
        Username = "peer",
        BatchId = "grab-batch",
        EnqueueDestination = "Artist - Album"
    };

    private static void Reject(SlskdRetryHandler handler, SlskdDownloadItem item) =>
        handler.OnFileStateChanged(item, new SlskdFileState(Transfer("rejected", "Completed, Rejected", "grab-batch")), new SlskdProviderSettings { RetryAttempts = 1 });

    [Fact]
    public void removal_returns_the_grab_batch_and_every_retry_recorded_before_it()
    {
        SlskdDownloadItem item = Grab();

        Assert.True(item.TryRecordRetryBatch("retry-batch"));

        Assert.Equal(["grab-batch", "retry-batch"], item.MarkRemoved().Order());
        Assert.True(item.Removed);
    }

    [Fact]
    public void a_retry_batch_after_removal_is_refused()
    {
        SlskdDownloadItem item = Grab();
        item.MarkRemoved();

        Assert.False(item.TryRecordRetryBatch("retry-batch"));
    }

    [Fact]
    public void a_removed_grab_is_not_retried()
    {
        FakeSlskd api = new();
        SlskdDownloadItem item = Grab();
        item.MarkRemoved();

        Reject(new SlskdRetryHandler(api, LogManager.CreateNullLogger()), item);

        Assert.Empty(api.Enqueued);
    }

    [Fact]
    public async Task a_retry_enqueued_while_the_grab_is_removed_cancels_only_its_own_batch()
    {
        FakeSlskd api = new();
        SlskdDownloadItem item = Grab();
        api.DuringEnqueue = () => item.MarkRemoved();
        api.Transfers.Add(Transfer("retry-transfer", "Queued, Remotely", "retry-batch"));
        api.Transfers.Add(Transfer("other-grab-transfer", "Queued, Remotely", "other-batch"));

        Reject(new SlskdRetryHandler(api, LogManager.CreateNullLogger()), item);
        await api.RemovedOne.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal([("retry-transfer", false), ("retry-transfer", true)], api.Deletes);
    }

    [Fact]
    public void a_retry_that_beats_the_removal_is_left_for_the_removal_to_cancel()
    {
        FakeSlskd api = new();
        SlskdDownloadItem item = Grab();
        api.Transfers.Add(Transfer("retry-transfer", "Queued, Remotely", "retry-batch"));

        Reject(new SlskdRetryHandler(api, LogManager.CreateNullLogger()), item);

        Assert.Single(api.Enqueued);
        Assert.Empty(api.Deletes);
        Assert.Contains("retry-batch", item.MarkRemoved());
    }

    [Fact]
    public async Task only_the_named_batches_are_cancelled_and_finished_transfers_only_removed()
    {
        FakeSlskd api = new();
        api.Transfers.Add(Transfer("live", "InProgress", "grab-batch"));
        api.Transfers.Add(Transfer("finished", "Completed, Succeeded", "grab-batch"));
        api.Transfers.Add(Transfer("polled", "InProgress", "grab-batch"));
        api.Transfers.Add(Transfer("unbatched", "InProgress", null));
        api.Transfers.Add(Transfer("another-grab", "InProgress", "other-batch"));

        int count = await SlskdBatchCanceller.CancelAsync(api, new SlskdProviderSettings(), "peer", new HashSet<string> { "grab-batch" }, new HashSet<string> { "polled" }, LogManager.CreateNullLogger());

        Assert.Equal(2, count);
        Assert.Equal([("live", false), ("live", true), ("finished", true)], api.Deletes);
    }

    [Fact]
    public async Task a_transfer_slskd_already_dropped_does_not_stop_the_rest()
    {
        FakeSlskd api = new();
        api.Transfers.Add(Transfer("gone", "Completed, Succeeded", "grab-batch"));
        api.Transfers.Add(Transfer("next", "Completed, Succeeded", "grab-batch"));
        api.FailingDeletes.Add("gone");

        await SlskdBatchCanceller.CancelAsync(api, new SlskdProviderSettings(), "peer", new HashSet<string> { "grab-batch" }, null, LogManager.CreateNullLogger());

        Assert.Equal([("next", true)], api.Deletes);
    }

    [Fact]
    public async Task a_failed_listing_cancels_nothing_and_does_not_throw()
    {
        FakeSlskd api = new() { FailListing = true };

        int count = await SlskdBatchCanceller.CancelAsync(api, new SlskdProviderSettings(), "peer", new HashSet<string> { "grab-batch" }, null, LogManager.CreateNullLogger());

        Assert.Equal(0, count);
        Assert.Empty(api.Deletes);
    }
}

// Through Lidarr's own DiskProviderBase on a real folder: only the platform members are stubbed.
public sealed class SlskdLeftoverFolderTests : IDisposable
{
    private sealed class LocalDisk() : DiskProviderBase(new FileSystem())
    {
        public override long? GetAvailableSpace(string path) => throw new NotSupportedException();
        public override void InheritFolderPermissions(string filename) => throw new NotSupportedException();
        public override void SetEveryonePermissions(string filename) => throw new NotSupportedException();
        public override void SetFilePermissions(string path, string mask, string group) => throw new NotSupportedException();
        public override void SetPermissions(string path, string mask, string group) => throw new NotSupportedException();
        public override void CopyPermissions(string sourcePath, string targetPath) => throw new NotSupportedException();
        public override long? GetTotalSize(string path) => throw new NotSupportedException();
        public override bool TryCreateHardLink(string source, string destination) => throw new NotSupportedException();
    }

    private static readonly DateTime Now = new(2026, 10, 28, 7, 9, 44, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sleezer-leftover-" + Guid.NewGuid().ToString("N"));
    private readonly LocalDisk _disk = new();

    public SlskdLeftoverFolderTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Folder(params string[] files)
    {
        string folder = Path.Combine(_root, "Artist - Album");
        Directory.CreateDirectory(folder);
        foreach (string file in files)
            File.WriteAllText(Path.Combine(folder, file), "audio");
        return folder;
    }

    [Fact]
    public void an_untracked_folder_holding_files_is_renamed_aside_with_its_files()
    {
        string folder = Folder("16 - Track.flac");

        string? aside = SlskdLeftoverFolder.MoveAside(_disk, folder, [], Now);

        Assert.Equal(folder + " (leftover 20261028-070944)", aside);
        Assert.False(Directory.Exists(folder));
        Assert.True(File.Exists(Path.Combine(aside!, "16 - Track.flac")));
    }

    [Fact]
    public void a_folder_a_tracked_grab_resolves_to_is_left_alone()
    {
        string folder = Folder("16 - Track.flac");

        Assert.Null(SlskdLeftoverFolder.MoveAside(_disk, folder, [folder + Path.DirectorySeparatorChar], Now));
        Assert.True(File.Exists(Path.Combine(folder, "16 - Track.flac")));
    }

    [Fact]
    public void a_tracked_folder_differing_only_by_case_does_not_protect_it()
    {
        string folder = Folder("16 - Track.flac");

        Assert.NotNull(SlskdLeftoverFolder.MoveAside(_disk, folder, [Path.Combine(_root, "artist - album")], Now));
    }

    [Fact]
    public void an_empty_or_missing_folder_is_left_alone()
    {
        Assert.Null(SlskdLeftoverFolder.MoveAside(_disk, Path.Combine(_root, "Missing"), [], Now));

        string empty = Folder();
        Assert.Null(SlskdLeftoverFolder.MoveAside(_disk, empty, [], Now));
        Assert.True(Directory.Exists(empty));
    }
}
