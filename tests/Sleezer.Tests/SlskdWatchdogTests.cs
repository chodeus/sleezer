using FluentValidation.Results;
using NLog;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek;
using Xunit;

namespace Sleezer.Tests;

// ProcessUserTransfers assigns a whole per-peer-directory transfer group to the
// item owning the group's FIRST file, so two items sharing a peer directory can
// see each other's transfers in FileStates. The watchdog cancels at slskd, so an
// unfiltered sweep here kills the other item's live download.
public class SlskdWatchdogTests
{
    private sealed class RecordingApiClient : ISlskdApiClient
    {
        public List<string> Deleted { get; } = [];

        public Task DeleteTransferAsync(SlskdProviderSettings settings, string username, string fileId, bool remove = false)
        {
            Deleted.Add(fileId);
            return Task.CompletedTask;
        }

        public Task<SlskdEnqueueResult> EnqueueDownloadAsync(SlskdProviderSettings settings, string username, IEnumerable<(string Filename, long Size)> files, string? externalId = null, string? destination = null) =>
            Task.FromResult(new SlskdEnqueueResult(null, [], []));
        public Task<List<SlskdUserTransfers>> GetAllTransfersAsync(SlskdProviderSettings settings, bool includeRemoved = false) =>
            Task.FromResult(new List<SlskdUserTransfers>());
        public Task<SlskdUserTransfers?> GetUserTransfersAsync(SlskdProviderSettings settings, string username) =>
            Task.FromResult<SlskdUserTransfers?>(null);
        public Task<SlskdDownloadFile?> GetTransferAsync(SlskdProviderSettings settings, string username, string fileId) =>
            Task.FromResult<SlskdDownloadFile?>(null);
        public Task<int?> GetQueuePositionAsync(SlskdProviderSettings settings, string username, string fileId) =>
            Task.FromResult<int?>(null);
        public Task DeleteAllCompletedAsync(SlskdProviderSettings settings) => Task.CompletedTask;
        public Task<string?> GetDownloadPathAsync(SlskdProviderSettings settings) =>
            Task.FromResult<string?>(null);
        public Task<SlskdDestinationConfig?> GetDestinationConfigAsync(SlskdProviderSettings settings) =>
            Task.FromResult<SlskdDestinationConfig?>(null);
        public Task<ValidationFailure?> TestConnectionAsync(SlskdProviderSettings settings) =>
            Task.FromResult<ValidationFailure?>(null);
        public Task<(List<SlskdEventRecord> Events, int TotalCount)> GetEventsAsync(SlskdProviderSettings settings, int offset, int limit) =>
            Task.FromResult((new List<SlskdEventRecord>(), 0));
    }

    private const string OwnedFile = @"@@peer\Artist\Album\01.flac";
    private const string ForeignFile = @"@@peer\Artist\Album\02.flac";

    // Queued past the position threshold is the one bailout that needs no aged
    // timestamps — FirstQueuedAt is only set on a second UpdateFile.
    private static SlskdDownloadFile StuckFile(string id, string filename) => new(
        Id: id,
        Username: "peer",
        Direction: "Download",
        Filename: filename,
        Size: 1000,
        StartOffset: 0,
        State: "Queued, Remotely",
        RequestedAt: DateTime.UtcNow,
        EnqueuedAt: DateTime.UtcNow,
        StartedAt: DateTime.MinValue,
        BytesTransferred: 0,
        AverageSpeed: 0,
        BytesRemaining: 1000,
        ElapsedTime: TimeSpan.Zero,
        PercentComplete: 0,
        RemainingTime: TimeSpan.Zero,
        EndedAt: null,
        PlaceInQueue: 9999);

    private static SlskdDownloadItem NewItem(params string[] filenames)
    {
        string source = "[" + string.Join(",", filenames.Select(f =>
            $"{{\"Filename\":{System.Text.Json.JsonSerializer.Serialize(f)},\"Size\":1000}}")) + "]";
        return new SlskdDownloadItem(new ReleaseInfo { Source = source, Title = "t", DownloadUrl = "u" })
        {
            Username = "peer"
        };
    }

    [Fact]
    public async Task Watchdog_cancels_only_the_files_this_item_enqueued()
    {
        SlskdDownloadItem item = NewItem(OwnedFile);

        // Shared peer directory: slskd reports both items' transfers in one group.
        item.SlskdDownloadDirectory = new SlskdDownloadDirectory(
            @"@@peer\Artist\Album",
            2,
            [StuckFile("owned-id", OwnedFile), StuckFile("foreign-id", ForeignFile)]);

        Assert.Equal(2, item.FileStates.Count);

        RecordingApiClient api = new();
        SlskdProviderSettings settings = new() { MaxQueuePositionBeforeCancel = 500 };

        await new SlskdWatchdog(api, LogManager.GetLogger("tests"))
            .InspectAsync(item, settings, CancellationToken.None);

        Assert.Equal(["owned-id"], api.Deleted);
    }

    // slskd creates no transfer for a file it rejected, so one under that name
    // is another item's — cancelling it would kill a live download.
    [Fact]
    public async Task Watchdog_leaves_a_transfer_slskd_rejected_for_this_item_alone()
    {
        SlskdDownloadItem item = NewItem(OwnedFile, ForeignFile);
        item.MarkEnqueueFailed([ForeignFile]);

        item.SlskdDownloadDirectory = new SlskdDownloadDirectory(
            @"@@peer\Artist\Album",
            2,
            [StuckFile("owned-id", OwnedFile), StuckFile("rejected-id", ForeignFile)]);

        RecordingApiClient api = new();
        SlskdProviderSettings settings = new() { MaxQueuePositionBeforeCancel = 500 };

        await new SlskdWatchdog(api, LogManager.GetLogger("tests"))
            .InspectAsync(item, settings, CancellationToken.None);

        Assert.Equal(["owned-id"], api.Deleted);
    }
}
