using FluentValidation.Results;
using NLog;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Music;
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

    private static SlskdDownloadFile ErroredFile(string? batchId = null) => new(
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
        EndedAt: null,
        BatchId: batchId);

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

    // A file that is already failed raises FileStateChanged as the directory is attached, so the
    // batch must be recovered first.
    [Fact]
    public void a_batch_recovered_after_a_restart_retries_into_the_grab_destination()
    {
        EnqueueRecorder api = new();
        SlskdProviderSettings settings = new() { RetryAttempts = 2 };
        SlskdRetryHandler handler = new(api, LogManager.CreateNullLogger());
        SlskdDownloadItem item = new(new ReleaseInfo
        {
            Source = $"[{{\"Filename\":{System.Text.Json.JsonSerializer.Serialize(Track)},\"Size\":1000}}]",
            Title = "t",
            DownloadUrl = "u"
        })
        {
            Username = "peer",
            ResolvedAlbum = new Album { Title = "Album", Artist = new LazyLoaded<Artist>(new Artist { Name = "Artist" }) }
        };
        item.FileStateChanged += (_, fileState) => handler.OnFileStateChanged(item, fileState, settings);

        item.SlskdDownloadDirectory = new SlskdDownloadDirectory(@"@@peer\Artist\Album", 1, [ErroredFile(batchId: "batch-id")]);

        Assert.Equal(["Artist - Album"], api.Destinations);
    }

    [Fact]
    public void a_batch_id_on_another_item_s_file_is_not_adopted()
    {
        SlskdDownloadItem item = new(new ReleaseInfo
        {
            Source = $"[{{\"Filename\":{System.Text.Json.JsonSerializer.Serialize(Track)},\"Size\":1000}}]",
            Title = "t",
            DownloadUrl = "u"
        });
        SlskdDownloadFile foreign = ErroredFile(batchId: "other-batch") with { Filename = @"@@peer\Artist\Album\99.flac" };

        item.SlskdDownloadDirectory = new SlskdDownloadDirectory(@"@@peer\Artist\Album", 2, [ErroredFile(), foreign]);

        Assert.Null(item.BatchId);
        Assert.Null(item.EnqueueDestination);
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

    // A server holding the given events newest first; counts the pages read.
    private sealed class EventServer(IEnumerable<SlskdEventRecord> newestFirst)
    {
        private readonly List<SlskdEventRecord> _events = [.. newestFirst];
        public int PagesRead { get; private set; }

        public Task<List<SlskdEventRecord>> Page(int offset, int limit)
        {
            PagesRead++;
            return Task.FromResult(_events.Skip(offset).Take(limit).ToList());
        }
    }

    [Fact]
    public async Task the_first_poll_reads_one_page()
    {
        EventServer server = new(Enumerable.Range(1, 120).Reverse().Select(s => Event(s)));

        List<SlskdEventRecord> unseen = await new SlskdEventCursor().ReadUnseenAsync(server.Page, pageSize: 50);

        Assert.Equal(1, server.PagesRead);
        Assert.Equal(50, unseen.Count);
    }

    [Fact]
    public async Task a_burst_of_new_events_is_drained_across_every_page()
    {
        SlskdEventCursor cursor = new();
        SlskdEventRecord handled = Event(0);
        cursor.TakeUnseen([handled]);

        List<SlskdEventRecord> burst = [.. Enumerable.Range(1, 260).Select(s => Event(s))];
        EventServer server = new([.. Enumerable.Reverse(burst), handled]);

        List<SlskdEventRecord> unseen = await cursor.ReadUnseenAsync(server.Page, pageSize: 50);

        Assert.Equal(burst, unseen);
        Assert.Equal(6, server.PagesRead);
    }

    [Fact]
    public async Task an_event_sharing_the_newest_timestamp_on_the_next_page_is_still_read()
    {
        SlskdEventCursor cursor = new();
        SlskdEventRecord handled = Event(5);
        cursor.TakeUnseen([handled]);

        SlskdEventRecord fresh = Event(6), twin = Event(5);
        EventServer server = new([fresh, handled, twin, Event(4)]);

        Assert.Equal([twin, fresh], await cursor.ReadUnseenAsync(server.Page, pageSize: 2));
    }

    [Fact]
    public async Task a_failed_event_is_retried_on_later_polls_then_dropped()
    {
        SlskdEventCursor cursor = new();
        SlskdEventRecord failing = Event(1);
        EventServer server = new([failing]);
        List<bool> willRetry = [];
        int attempts = 0;

        for (int poll = 0; poll < SlskdEventCursor.MaxAttempts + 1; poll++)
        {
            await cursor.PollAsync(server.Page, 50,
                _ => { attempts++; throw new InvalidOperationException("slskd unavailable"); },
                (_, _, retry) => willRetry.Add(retry));
        }

        Assert.Equal(SlskdEventCursor.MaxAttempts, attempts);
        Assert.Equal([true, true, false], willRetry);
    }

    [Fact]
    public async Task a_poll_started_while_another_runs_is_skipped()
    {
        SlskdEventCursor cursor = new();
        EventServer server = new([Event(1)]);
        TaskCompletionSource release = new();

        Task first = cursor.PollAsync(server.Page, 50, _ => release.Task, (_, _, _) => { });
        await cursor.PollAsync(server.Page, 50, _ => Task.CompletedTask, (_, _, _) => { });
        release.SetResult();
        await first;

        Assert.Equal(1, server.PagesRead);
    }
}
