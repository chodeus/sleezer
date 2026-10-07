using FluentValidation.Results;
using NLog;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek;
using Xunit;

namespace Sleezer.Tests;

public class SlskdRetryDestinationTests
{
    private sealed class EnqueueRecorder : ISlskdApiClient
    {
        public List<string?> Destinations { get; } = [];

        public Task<SlskdEnqueueResult> EnqueueDownloadAsync(SlskdProviderSettings settings, string username, IEnumerable<(string Filename, long Size)> files, string? externalId = null, string? destination = null)
        {
            Destinations.Add(destination);
            return Task.FromResult(new SlskdEnqueueResult("batch", [.. files.Select(f => f.Filename)], []));
        }

        public Task DeleteTransferAsync(SlskdProviderSettings settings, string username, string fileId, bool remove = false) => Task.CompletedTask;
        public Task<List<SlskdUserTransfers>> GetAllTransfersAsync(SlskdProviderSettings settings, bool includeRemoved = false) =>
            Task.FromResult(new List<SlskdUserTransfers>());
        public Task<SlskdUserTransfers?> GetUserTransfersAsync(SlskdProviderSettings settings, string username) =>
            Task.FromResult<SlskdUserTransfers?>(null);
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

    private const string Track = @"@@peer\Artist\Album\01.flac";

    private static SlskdDownloadFile ErroredFile() => new(
        Id: "id",
        Username: "peer",
        Direction: "Download",
        Filename: Track,
        Size: 1000,
        StartOffset: 0,
        State: "Completed, Errored",
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
    public void retry_lands_in_the_destination_the_album_was_enqueued_to()
    {
        EnqueueRecorder api = new();
        SlskdDownloadItem item = new(new ReleaseInfo
        {
            Source = $"[{{\"Filename\":{System.Text.Json.JsonSerializer.Serialize(Track)},\"Size\":1000}}]",
            Title = "t",
            DownloadUrl = "u"
        })
        {
            Username = "peer",
            EnqueueDestination = "Artist - Album"
        };

        new SlskdRetryHandler(api, LogManager.CreateNullLogger())
            .OnFileStateChanged(item, new SlskdFileState(ErroredFile()), new SlskdProviderSettings { RetryAttempts = 2 });

        Assert.Equal(["Artist - Album"], api.Destinations);
    }
}

public class SlskdEventCursorTests
{
    private static readonly DateTime T0 = new(2026, 10, 7, 3, 0, 0, DateTimeKind.Utc);

    private static SlskdEventRecord Event(int secondsAfterT0, Guid? id = null) =>
        new() { Id = id ?? Guid.NewGuid(), Timestamp = T0.AddSeconds(secondsAfterT0), Type = "DownloadDirectoryComplete", Data = "{}" };

    // slskd lists events newest first.
    private static List<SlskdEventRecord> Page(params SlskdEventRecord[] newestFirst) => [.. newestFirst];

    [Fact]
    public void later_polls_return_only_events_newer_than_the_last_handled_one()
    {
        SlskdEventCursor cursor = new();
        SlskdEventRecord old1 = Event(1), old2 = Event(2);
        Assert.Equal([old1, old2], cursor.TakeUnseen(Page(old2, old1)));

        SlskdEventRecord fresh = Event(3);
        Assert.Equal([fresh], cursor.TakeUnseen(Page(fresh, old2, old1)));
        Assert.Empty(cursor.TakeUnseen(Page(fresh, old2, old1)));
    }

    [Fact]
    public void an_event_sharing_the_newest_timestamp_is_still_handled_once()
    {
        SlskdEventCursor cursor = new();
        SlskdEventRecord first = Event(5);
        cursor.TakeUnseen(Page(first));

        SlskdEventRecord twin = Event(5);
        Assert.Equal([twin], cursor.TakeUnseen(Page(twin, first)));
        Assert.Empty(cursor.TakeUnseen(Page(twin, first)));
    }

    [Fact]
    public void a_full_page_of_unseen_events_asks_for_the_next_page_only_after_the_first_poll()
    {
        SlskdEventCursor cursor = new();
        List<SlskdEventRecord> full = Page(Event(2), Event(1));
        Assert.False(cursor.NeedsNextPage(full, pageSize: 2));

        cursor.TakeUnseen(full);
        Assert.True(cursor.NeedsNextPage(Page(Event(4), Event(3)), pageSize: 2));
        Assert.False(cursor.NeedsNextPage(Page(Event(4), full[0]), pageSize: 2));
        Assert.False(cursor.NeedsNextPage(Page(Event(5)), pageSize: 2));
    }
}
