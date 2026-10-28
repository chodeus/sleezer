using System.IO.Abstractions;
using System.Net;
using FluentValidation.Results;
using NLog;
using NLog.Config;
using NLog.Targets;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Http;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek;
using Xunit;

namespace Sleezer.Tests;

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
        public HashSet<string> GoneDeletes { get; } = [];
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
            if (GoneDeletes.Contains(fileId))
                throw new HttpException(new HttpResponse(new HttpRequest("http://slskd/api/v0/transfers"), new HttpHeader(), string.Empty, HttpStatusCode.NotFound));
            if (FailingDeletes.Contains(fileId))
                throw new InvalidOperationException("slskd error");
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
    public void a_retry_batch_after_removal_is_refused_but_still_recorded_for_later_cancels()
    {
        SlskdDownloadItem item = Grab();
        item.MarkRemoved();

        Assert.False(item.TryRecordRetryBatch("retry-batch"));
        Assert.Contains("retry-batch", item.EnqueuedBatches());
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
        await api.RemovedOne.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal([("retry-transfer", false), ("retry-transfer", true)], api.Deletes);
    }

    [Fact]
    public void a_retry_that_beats_the_removal_is_handed_to_it()
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

    private static (Logger Logger, MemoryTarget Logs) CapturingLogger()
    {
        MemoryTarget logs = new() { Layout = "${level}|${message}" };
        LoggingConfiguration config = new();
        config.AddRuleForAllLevels(logs);
        return (new LogFactory { Configuration = config }.GetLogger("canceller"), logs);
    }

    [Fact]
    public async Task a_transfer_slskd_already_dropped_is_skipped_quietly()
    {
        FakeSlskd api = new();
        api.Transfers.Add(Transfer("gone", "Completed, Succeeded", "grab-batch"));
        api.Transfers.Add(Transfer("next", "Completed, Succeeded", "grab-batch"));
        api.GoneDeletes.Add("gone");
        (Logger logger, MemoryTarget logs) = CapturingLogger();

        int count = await SlskdBatchCanceller.CancelAsync(api, new SlskdProviderSettings(), "peer", new HashSet<string> { "grab-batch" }, null, logger);

        Assert.Equal(1, count);
        Assert.Equal([("next", true)], api.Deletes);
        Assert.Equal(["Debug|Transfer \"gone\" (\"01.flac\") was already gone"], logs.Logs);
    }

    [Fact]
    public async Task a_delete_slskd_refuses_is_warned_and_does_not_stop_the_rest()
    {
        FakeSlskd api = new();
        api.Transfers.Add(Transfer("refused", "Completed, Succeeded", "grab-batch"));
        api.Transfers.Add(Transfer("next", "Completed, Succeeded", "grab-batch"));
        api.FailingDeletes.Add("refused");
        (Logger logger, MemoryTarget logs) = CapturingLogger();

        int count = await SlskdBatchCanceller.CancelAsync(api, new SlskdProviderSettings(), "peer", new HashSet<string> { "grab-batch" }, null, logger);

        Assert.Equal(1, count);
        Assert.Equal([("next", true)], api.Deletes);
        Assert.Equal(["Warn|Could not \"remove\" transfer \"refused\" (\"01.flac\"); the next poll retries"], logs.Logs);
    }

    [Fact]
    public async Task a_failed_listing_cancels_nothing_and_does_not_throw()
    {
        FakeSlskd api = new() { FailListing = true };

        int count = await SlskdBatchCanceller.CancelAsync(api, new SlskdProviderSettings(), "peer", new HashSet<string> { "grab-batch" }, null, LogManager.CreateNullLogger());

        Assert.Equal(0, count);
        Assert.Empty(api.Deletes);
    }

    private static SlskdRemovedGrab Removed(SlskdDownloadItem item, params SlskdDownloadFile[] lastPoll)
    {
        item.SlskdDownloadDirectory = new SlskdDownloadDirectory(@"@@peer\Artist\Album", lastPoll.Length, [.. lastPoll]);
        item.MarkRemoved();
        return new SlskdRemovedGrab(item, DateTime.UtcNow);
    }

    [Fact]
    public void its_leftovers_are_its_batches_and_its_last_poll_s_transfers()
    {
        SlskdDownloadItem item = Grab();
        SlskdRemovedGrab removed = Removed(item, Transfer("polled", "InProgress", null));
        item.TryRecordRetryBatch("retry-batch");

        List<SlskdDownloadFile> leftovers = removed.Leftovers(
        [
            Transfer("polled", "InProgress", null),
            Transfer("grab", "Queued, Remotely", "grab-batch"),
            Transfer("retry", "Queued, Remotely", "retry-batch"),
            Transfer("another-grab", "Queued, Remotely", "other-batch"),
            Transfer("unbatched", "Queued, Remotely", null)
        ]);

        Assert.Equal(["grab", "polled", "retry"], leftovers.Select(f => f.Id).Order());
    }

    [Fact]
    public void a_cancel_claim_holds_off_only_its_own_directory()
    {
        SlskdRemovedGrab removed = Removed(Grab());
        DateTime now = DateTime.UtcNow;

        Assert.True(removed.TryClaimCancel("CD1", now));
        Assert.True(removed.TryClaimCancel("CD2", now));
        Assert.False(removed.TryClaimCancel("CD1", now.AddSeconds(10)));
        Assert.True(removed.TryClaimCancel("CD1", now.AddSeconds(30)));
    }

    [Fact]
    public void it_settles_only_once_its_peer_lists_none_of_its_transfers()
    {
        SlskdRemovedGrab removed = Removed(Grab());

        removed.SettleUnlessListed([Transfer("grab", "Queued, Remotely", "grab-batch")], removed.BatchCount);
        Assert.False(removed.Settled);

        removed.SettleUnlessListed([Transfer("another-grab", "Queued, Remotely", "other-batch")], removed.BatchCount);
        Assert.True(removed.Settled);
    }

    [Fact]
    public void a_retry_batch_recorded_while_its_peer_was_listed_keeps_it_unsettled()
    {
        SlskdDownloadItem item = Grab();
        SlskdRemovedGrab removed = Removed(item);
        int beforeListing = removed.BatchCount;

        item.TryRecordRetryBatch("retry-batch");
        removed.SettleUnlessListed([], beforeListing);

        Assert.False(removed.Settled);
    }

    [Fact]
    public void a_retry_batch_recorded_after_it_settled_reopens_it()
    {
        SlskdDownloadItem item = Grab();
        SlskdRemovedGrab removed = Removed(item);
        removed.SettleUnlessListed([], removed.BatchCount);

        item.TryRecordRetryBatch("retry-batch");

        Assert.False(removed.Settled);
    }

    [Fact]
    public void a_removal_lidarr_reports_after_the_grab_was_untracked_keeps_its_transfers()
    {
        SlskdRemovedGrab removed = Removed(Grab());
        DateTime later = DateTime.UtcNow.AddMinutes(5);

        SlskdRemovedGrab again = removed.RemovedAgain(null, later);
        Assert.Same(removed, again);
        Assert.Equal(["grab"], again.Leftovers([Transfer("grab", "Queued, Remotely", "grab-batch")]).Select(f => f.Id));

        Assert.Equal(later, removed.RemovedAgain(Grab(), later).At);
    }

    [Fact]
    public void a_removed_grab_with_no_peer_has_nothing_to_poll_for()
    {
        Assert.True(new SlskdRemovedGrab(null, DateTime.UtcNow).Settled);
    }

    private static SlskdDownloadItem Polled(params SlskdDownloadFile[] transfers)
    {
        SlskdDownloadItem item = Grab();
        item.SlskdDownloadDirectory = new SlskdDownloadDirectory(@"@@peer\Artist\Album", transfers.Length, [.. transfers]);
        return item;
    }

    // slskd marks every finished state Completed; "Requested" isn't in the parsed set and still runs.
    [Theory]
    [InlineData("Requested", true)]
    [InlineData("Queued, Remotely", true)]
    [InlineData("InProgress", true)]
    [InlineData("Completed, Succeeded", false)]
    [InlineData("Completed, Errored", false)]
    public void a_grab_runs_while_a_transfer_of_its_own_is_unfinished(string state, bool running)
    {
        Assert.Equal(running, Polled(Transfer("t", state, "grab-batch")).HasLiveTransfers());
    }

    [Fact]
    public void another_grab_s_running_transfer_in_the_same_peer_folder_is_not_its_own()
    {
        SlskdDownloadFile other = Transfer("other", "InProgress", "other-batch") with { Filename = @"@@peer\Artist\Album\02.flac" };

        Assert.False(Polled(Transfer("t", "Completed, Succeeded", "grab-batch"), other).HasLiveTransfers());
    }

    // Lidarr's history decides unless the grab still downloads; an attempt the new grab replaces holds only while it downloads.
    [Fact]
    public void the_folder_is_held_by_a_grab_lidarr_still_has_or_one_still_downloading()
    {
        SlskdDownloadItem finished = Polled(Transfer("a", "Completed, Succeeded", "grab-batch"));
        SlskdDownloadItem queued = Polled(Transfer("b", "Completed, Succeeded", "grab-batch"));
        SlskdDownloadItem downloading = Polled(Transfer("c", "InProgress", "grab-batch"));
        HashSet<SlskdDownloadItem> doneInLidarr = [finished, downloading];

        Assert.Null(SlskdLeftoverFolder.Holder([finished], [], doneInLidarr.Contains));
        Assert.Same(queued, SlskdLeftoverFolder.Holder([finished, queued], [], doneInLidarr.Contains));
        Assert.Same(downloading, SlskdLeftoverFolder.Holder([finished, downloading], [], doneInLidarr.Contains));
        Assert.Null(SlskdLeftoverFolder.Holder([queued], [queued], doneInLidarr.Contains));
        Assert.Same(downloading, SlskdLeftoverFolder.Holder([downloading], [downloading], doneInLidarr.Contains));
    }

    [Fact]
    public void a_folder_is_held_by_one_grab_through_any_client_until_its_claim_is_released()
    {
        SlskdDestinationClaims claims = new();
        string downloads = Path.Combine(Path.GetTempPath(), "downloads");
        string folder = Path.Combine(downloads, "Artist - Album");

        IDisposable? first = claims.TryHold(folder);
        Assert.NotNull(first);
        Assert.Null(claims.TryHold(folder));
        Assert.Null(claims.TryHold(folder + Path.DirectorySeparatorChar));
        Assert.NotNull(claims.TryHold(Path.Combine(downloads, "Artist - Other Album")));
        Assert.NotNull(claims.TryHold(Path.Combine(Path.GetTempPath(), "other downloads", "Artist - Album")));

        first.Dispose();
        Assert.NotNull(claims.TryHold(folder));
    }

    [Fact]
    public void a_name_differing_from_the_held_folder_only_by_case_or_accent_form_is_held_too()
    {
        SlskdDestinationClaims claims = new();
        string downloads = Path.Combine(Path.GetTempPath(), "downloads");
        Assert.NotNull(claims.TryHold(Path.Combine(downloads, "Caf\u00e9 - Album")));

        Assert.Null(claims.TryHold(Path.Combine(downloads, "Cafe\u0301 - Album")));
        Assert.Null(claims.TryHold(Path.Combine(downloads, "caf\u00e9 - album")));
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
    public void a_folder_holding_files_is_renamed_aside_with_its_files()
    {
        string folder = Folder("16 - Track.flac");

        string? aside = SlskdLeftoverFolder.MoveAside(_disk, folder, Now);

        Assert.Equal(folder + " (leftover 20261028-070944)", aside);
        Assert.False(Directory.Exists(folder));
        Assert.True(File.Exists(Path.Combine(aside!, "16 - Track.flac")));
    }

    [Fact]
    public void an_empty_or_missing_folder_is_left_alone()
    {
        Assert.Null(SlskdLeftoverFolder.MoveAside(_disk, Path.Combine(_root, "Missing"), Now));

        string empty = Folder();
        Assert.Null(SlskdLeftoverFolder.MoveAside(_disk, empty, Now));
        Assert.True(Directory.Exists(empty));
    }

    [Fact]
    public void only_a_folder_with_something_in_it_holds_files()
    {
        Assert.False(SlskdLeftoverFolder.HoldsFiles(_disk, Path.Combine(_root, "Missing")));
        Assert.False(SlskdLeftoverFolder.HoldsFiles(_disk, Folder()));
        Assert.True(SlskdLeftoverFolder.HoldsFiles(_disk, Folder("01.flac")));
    }

    // The renamed folder's name must fit in 255 bytes; the accented name only fits when the cut counts bytes, not characters.
    [Theory]
    [InlineData('a', 235)]
    [InlineData('\u00e9', 115)]
    public void a_long_folder_name_is_cut_short_so_the_rename_fits(char letter, int count)
    {
        string folder = Path.Combine(_root, "Artist - " + new string(letter, count));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "01.flac"), "audio");

        string? aside = SlskdLeftoverFolder.MoveAside(_disk, folder, Now);

        Assert.True(File.Exists(Path.Combine(aside!, "01.flac")));
        Assert.EndsWith(" (leftover 20261028-070944)", aside);
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(Path.GetFileName(aside)!), 250, 255);
    }

    private static SlskdDownloadItem InFolder(string subdirectory) =>
        new(new ReleaseInfo { Source = "[]", Title = subdirectory }) { DerivedSubdirectory = subdirectory };

    // Two clients can sit behind one slskd: each one's grabs in the folder count, and a client with none still counts.
    // A client rooted inside the folder keeps all its grabs there.
    [Fact]
    public void every_client_whose_root_holds_the_folder_or_lies_inside_it_is_listed_with_its_grabs_inside_it()
    {
        string folder = Path.Combine(_root, "Artist - Album");
        SlskdDownloadItem queued = InFolder("Artist - Album"), disc = InFolder(Path.Combine("Artist - Album", "CD1"));
        SlskdDownloadItem sibling = InFolder("Artist - Other Album"), elsewhere = InFolder("Artist - Album"), nested = InFolder("Other - Album");

        List<(int DefinitionId, List<SlskdDownloadItem> Owners)> clients = SlskdLeftoverFolder.ClientsHolding(folder, [
            (1, new OsPath(_root), [sibling]),
            (2, new OsPath(_root), [queued, disc]),
            (3, new OsPath(Path.Combine(_root, "other")), [elsewhere]),
            (4, new OsPath(string.Empty), []),
            (5, new OsPath(Path.Combine(folder, "nested")), [nested])]);

        Assert.Equal([1, 2, 5], clients.Select(c => c.DefinitionId));
        Assert.Empty(clients[0].Owners);
        Assert.Equal([queued, disc], clients[1].Owners);
        Assert.Equal([nested], clients[2].Owners);
    }

    [Fact]
    public void a_folder_holds_itself_and_its_disc_subfolders()
    {
        string folder = Path.Combine(_root, "Artist - Album");

        Assert.True(SlskdLeftoverFolder.Contains(folder, folder + Path.DirectorySeparatorChar));
        Assert.True(SlskdLeftoverFolder.Contains(folder, Path.Combine(folder, "CD1")));
    }

    [Fact]
    public void a_sibling_whose_name_extends_the_folder_s_is_not_inside_it()
    {
        string folder = Path.Combine(_root, "Artist - Album");

        Assert.False(SlskdLeftoverFolder.Contains(folder, folder + " (Deluxe)"));
    }

    // On any OS: the volume, not the OS, decides whether the two spellings are one folder.
    [Fact]
    public void a_name_differing_only_by_case_or_accent_form_is_the_same_folder()
    {
        Assert.True(SlskdLeftoverFolder.Contains(Path.Combine(_root, "Caf\u00e9 - Album"), Path.Combine(_root, "Cafe\u0301 - Album")));
        Assert.True(SlskdLeftoverFolder.Contains(Path.Combine(_root, "Artist - Album"), Path.Combine(_root, "artist - album", "CD1")));
    }
}
