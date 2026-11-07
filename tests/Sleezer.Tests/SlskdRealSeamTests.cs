using System.Text.Json;
using FluentValidation.Results;
using NLog;
using NzbDrone.Core.Download;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek;
using Xunit;

namespace Sleezer.Tests;

// Through the real parser, file state, resolver, tracker and watchdog; only the HTTP client is faked.
public class SlskdRealSeamTests
{
    private const string Dir = @"@@peer\Artist\Album";
    private const string Name = Dir + @"\01.flac";
    private static readonly DateTime Now = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);

    private static SlskdDownloadItem NewItem(params string[] filenames)
    {
        string source = "[" + string.Join(",", filenames.Select(f => $"{{\"Filename\":{JsonSerializer.Serialize(f)},\"Size\":1000}}")) + "]";
        return new SlskdDownloadItem(new ReleaseInfo { Source = source, Title = "t", DownloadUrl = "u" }) { Username = "peer" };
    }

    // Through the real slskd JSON parser: requestedAt/enqueuedAt have no zone suffix, startedAt has Z.
    private static SlskdDownloadFile Parse(string id, string state, string? requestedAt, string? enqueuedAt, string? startedAt, int? placeInQueue = null)
    {
        Dictionary<string, object?> o = new()
        {
            ["id"] = id, ["username"] = "peer", ["direction"] = "Download", ["filename"] = Name,
            ["size"] = 1000, ["startOffset"] = 0, ["state"] = state,
            ["bytesTransferred"] = 0, ["averageSpeed"] = 0.0, ["bytesRemaining"] = 1000, ["percentComplete"] = 0.0,
        };
        if (requestedAt != null) o["requestedAt"] = requestedAt;
        if (enqueuedAt != null) o["enqueuedAt"] = enqueuedAt;
        if (startedAt != null) o["startedAt"] = startedAt;
        if (placeInQueue != null) o["placeInQueue"] = placeInQueue;
        string json = "[" + JsonSerializer.Serialize(o) + "]";
        return SlskdDownloadFile.GetFiles(JsonDocument.Parse(json).RootElement).Single();
    }

    private static string Unzoned(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ss.fffffff");

    // A peer's rejection sets only requestedAt; with the retry's enqueue failed, nothing else ever moves.
    [Fact]
    public void A_rejected_file_whose_retry_never_produced_a_new_transfer_still_fails_after_twice_the_timeout()
    {
        SlskdDownloadItem item = NewItem(Name);
        SlskdDownloadFile rejected = Parse("t1", "Completed, Rejected", Unzoned(Now.AddHours(-5)), null, null);
        item.SlskdDownloadDirectory = new SlskdDownloadDirectory(Dir, 1, [rejected]);

        SlskdFileState state = item.FileStates[Name];
        state.UpdateMaxRetryCount(3);
        state.IncrementAttempt();
        item.SlskdDownloadDirectory = new SlskdDownloadDirectory(Dir, 1, [rejected]);

        SlskdStatusResolver.DownloadStatus resolved = SlskdStatusResolver.Resolve(item, TimeSpan.FromMinutes(60), Now);

        Assert.Equal(DownloadItemStatus.Failed, resolved.Status);
    }

    [Fact]
    public void Queue_timeout_through_the_real_parser_uses_the_unzoned_utc_request_time()
    {
        SlskdDownloadItem old = NewItem(Name);
        old.SlskdDownloadDirectory = new SlskdDownloadDirectory(Dir, 1, [Parse("a", "Queued, Remotely", Unzoned(Now.AddMinutes(-61)), null, null)]);
        SlskdDownloadItem recent = NewItem(Name);
        recent.SlskdDownloadDirectory = new SlskdDownloadDirectory(Dir, 1, [Parse("b", "Queued, Remotely", Unzoned(Now.AddMinutes(-59)), null, null)]);

        Assert.Equal(DownloadItemStatus.Failed, SlskdStatusResolver.Resolve(old, TimeSpan.FromMinutes(60), Now).Status);
        Assert.Equal(DownloadItemStatus.Queued, SlskdStatusResolver.Resolve(recent, TimeSpan.FromMinutes(60), Now).Status);
    }

    [Fact]
    public void A_second_client_with_bans_off_does_not_lift_the_first_clients_bans()
    {
        SlskdCorruptUserTracker tracker = new(LogManager.GetLogger("tests"));
        tracker.UseThreshold(1, 2);
        tracker.RecordStrike("bad", 2, 2);
        Assert.Contains("bad", tracker.GetBannedUsers());

        tracker.UseThreshold(2, 0);

        Assert.Contains("bad", tracker.GetBannedUsers());
    }

    private sealed class RacingApiClient(Action duringDelete) : ISlskdApiClient
    {
        public List<string> Deleted { get; } = [];
        public Task DeleteTransferAsync(SlskdProviderSettings settings, string username, string fileId, bool remove = false)
        {
            Deleted.Add(fileId);
            duringDelete();
            return Task.CompletedTask;
        }
        public Task<SlskdEnqueueResult> EnqueueDownloadAsync(SlskdProviderSettings settings, string username, IEnumerable<(string Filename, long Size)> files, string? externalId = null, string? destination = null) => Task.FromResult(new SlskdEnqueueResult(null, [], []));
        public Task<List<SlskdUserTransfers>> GetAllTransfersAsync(SlskdProviderSettings settings, bool includeRemoved = false) => Task.FromResult(new List<SlskdUserTransfers>());
        public Task<SlskdUserTransfers?> GetUserTransfersAsync(SlskdProviderSettings settings, string username) => Task.FromResult<SlskdUserTransfers?>(null);
        public Task<SlskdDownloadFile?> GetTransferAsync(SlskdProviderSettings settings, string username, string fileId) => Task.FromResult<SlskdDownloadFile?>(null);
        public Task<int?> GetQueuePositionAsync(SlskdProviderSettings settings, string username, string fileId) => Task.FromResult<int?>(null);
        public Task DeleteAllCompletedAsync(SlskdProviderSettings settings) => Task.CompletedTask;
        public Task<string?> GetDownloadPathAsync(SlskdProviderSettings settings) => Task.FromResult<string?>(null);
        public Task<SlskdDestinationConfig?> GetDestinationConfigAsync(SlskdProviderSettings settings) => Task.FromResult<SlskdDestinationConfig?>(null);
        public Task<ValidationFailure?> TestConnectionAsync(SlskdProviderSettings settings) => Task.FromResult<ValidationFailure?>(null);
        public Task<(List<SlskdEventRecord> Events, int TotalCount)> GetEventsAsync(SlskdProviderSettings settings, int offset, int limit) => Task.FromResult((new List<SlskdEventRecord>(), 0));
        public Task<bool> SendPrivateMessageAsync(SlskdProviderSettings settings, string username, string message) => Task.FromResult(false);
        public Task<bool> AcknowledgeConversationAsync(SlskdProviderSettings settings, string username) => Task.FromResult(false);
    }

    [Fact]
    public async Task The_watchdog_marks_the_transfer_it_deleted()
    {
        SlskdDownloadItem item = NewItem(Name);
        item.SlskdDownloadDirectory = new SlskdDownloadDirectory(Dir, 1, [Parse("first", "Queued, Remotely", Unzoned(Now), Unzoned(Now), null, 9999)]);
        SlskdFileState state = item.FileStates[Name];
        RacingApiClient api = new(() => { });

        await new SlskdWatchdog(api, LogManager.GetLogger("tests"))
            .InspectAsync(item, new SlskdProviderSettings { MaxQueuePositionBeforeCancel = 500 }, CancellationToken.None);

        Assert.Equal(["first"], api.Deleted);
        Assert.True(state.WatchdogCancelled);
    }

    [Fact]
    public async Task The_flag_names_the_transfer_the_watchdog_deleted_not_the_one_a_concurrent_poll_installed()
    {
        SlskdDownloadItem item = NewItem(Name);
        item.SlskdDownloadDirectory = new SlskdDownloadDirectory(Dir, 1, [Parse("first", "Queued, Remotely", Unzoned(Now), Unzoned(Now), null, 9999)]);
        SlskdFileState state = item.FileStates[Name];

        // A poll during the DELETE installs the retry's transfer.
        RacingApiClient api = new(() =>
            item.SlskdDownloadDirectory = new SlskdDownloadDirectory(Dir, 1, [Parse("retry", "Queued, Remotely", Unzoned(Now), Unzoned(Now), null, 1)]));

        await new SlskdWatchdog(api, LogManager.GetLogger("tests"))
            .InspectAsync(item, new SlskdProviderSettings { MaxQueuePositionBeforeCancel = 500 }, CancellationToken.None);

        Assert.Equal(["first"], api.Deleted);
        Assert.Equal("retry", state.File.Id);
        Assert.False(state.WatchdogCancelled);
    }
}
