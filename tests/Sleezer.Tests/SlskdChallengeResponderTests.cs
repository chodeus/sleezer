using System.Net;
using System.Text;
using System.Text.Json;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek;
using Xunit;

namespace Sleezer.Tests;

public class SlskdChallengeAnswerTests
{
    [Theory]
    [InlineData("Hi there! To download from me, reply to this message with the word: lantern. Then retry your download.", "lantern")]
    [InlineData("Human check for your requested album \"Artist / Artist - Album - 2001 - Title\". Reply only with this word / Antworte nur mit diesem Wort: HARBOUR. This unlocks downloads for 7 days.", "HARBOUR")]
    [InlineData("ProveIt: To prove you are a human downloading these files, please type \"open sesame\" in this chat to be added to my whitelist.", "open sesame")]
    [InlineData("ProveIt: To prove you are a human downloading these files, please type \" quiet harbour \" in this chat to be added to my whitelist.", "quiet harbour")]
    [InlineData("hey, to download from me please type \"blue moon\" here, then retry your download. thanks", "blue moon")]
    [InlineData("To download, please type “north star” in this chat.", "north star")]
    public void a_download_check_yields_the_word_it_asks_for(string text, string expected) =>
        Assert.Equal(expected, SlskdChallengeResponder.FindAnswer(text));

    [Theory]
    [InlineData("Wrong answer. 2 attempt(s) remaining.")]
    [InlineData("Correct. Downloads are unlocked for 7 days.")]
    [InlineData("ProveIt: You are verified. I will now retry your queued downloads automatically.")]
    [InlineData("Thanks for sharing!")]
    [InlineData("[Automatic Message] Please try browsing me if you get 'File not shared' errors.")]
    [InlineData("type \"hello\" here if you want to chat")]
    [InlineData("To download, please type \"this phrase is far too long to be any real check\" in this chat.")]
    public void other_messages_yield_nothing(string text) =>
        Assert.Null(SlskdChallengeResponder.FindAnswer(text));
}

public class SlskdPrivateMessageTests
{
    [Fact]
    public void reads_the_event_payload_slskd_sends_with_its_time_in_utc()
    {
        SlskdPrivateMessage? message = SlskdPrivateMessage.Parse(Payload("peer", "hello", "2026-01-02T03:04:05Z"));

        Assert.NotNull(message);
        Assert.Equal("peer", message.Username);
        Assert.Equal("hello", message.Text);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), message.SentAtUtc);
        Assert.Equal(DateTimeKind.Utc, message.SentAtUtc.Kind);
    }

    [Fact]
    public void an_outbound_message_is_not_read_as_one_received() =>
        Assert.Null(SlskdPrivateMessage.Parse(Payload("peer", "hello", "2026-01-02T03:04:05Z", direction: "Out")));

    internal static string Payload(string username, string text, string timestamp, string direction = "In") =>
        JsonSerializer.Serialize(new
        {
            version = 0,
            message = new { timestamp, id = 1, username, direction, message = text, isAcknowledged = false, wasReplayed = false }
        });
}

public class SlskdChallengeResponderTests
{
    private const string Check = "To download from me, reply to this message with the word: lantern. Then retry your download.";
    private static readonly DateTime GrabbedAt = new(2026, 1, 2, 3, 0, 0, DateTimeKind.Utc);

    private sealed class MessageRecorder : ISlskdApiClient
    {
        public List<(string BaseUrl, string Username, string Message)> Sent { get; } = [];
        public List<string> Acknowledged { get; } = [];
        public Func<bool> SendResult { get; set; } = () => true;

        public Task<bool> SendPrivateMessageAsync(SlskdProviderSettings settings, string username, string message)
        {
            Sent.Add((settings.BaseUrl, username, message));
            return Task.FromResult(SendResult());
        }

        public Task<bool> AcknowledgeConversationAsync(SlskdProviderSettings settings, string username)
        {
            Acknowledged.Add(username);
            return Task.FromResult(true);
        }

        public Task<SlskdEnqueueResult> EnqueueDownloadAsync(SlskdProviderSettings settings, string username, IEnumerable<(string Filename, long Size)> files, string? externalId = null, string? destination = null) =>
            throw new NotSupportedException();
        public Task<List<SlskdUserTransfers>> GetAllTransfersAsync(SlskdProviderSettings settings, bool includeRemoved = false) => throw new NotSupportedException();
        public Task<SlskdUserTransfers?> GetUserTransfersAsync(SlskdProviderSettings settings, string username) => throw new NotSupportedException();
        public Task<SlskdDownloadFile?> GetTransferAsync(SlskdProviderSettings settings, string username, string fileId) => throw new NotSupportedException();
        public Task<int?> GetQueuePositionAsync(SlskdProviderSettings settings, string username, string fileId) => throw new NotSupportedException();
        public Task DeleteTransferAsync(SlskdProviderSettings settings, string username, string fileId, bool remove = false) => throw new NotSupportedException();
        public Task DeleteAllCompletedAsync(SlskdProviderSettings settings) => throw new NotSupportedException();
        public Task<string?> GetDownloadPathAsync(SlskdProviderSettings settings) => throw new NotSupportedException();
        public Task<SlskdDestinationConfig?> GetDestinationConfigAsync(SlskdProviderSettings settings) => throw new NotSupportedException();
        public Task<ValidationFailure?> TestConnectionAsync(SlskdProviderSettings settings) => throw new NotSupportedException();
        public Task<(List<SlskdEventRecord> Events, int TotalCount)> GetEventsAsync(SlskdProviderSettings settings, int offset, int limit) => throw new NotSupportedException();
    }

    private sealed class Clock
    {
        public DateTime Now { get; set; } = GrabbedAt.AddMinutes(5);
        public List<TimeSpan> Waits { get; } = [];

        public Task Wait(TimeSpan wait)
        {
            Waits.Add(wait);
            Now += wait;
            return Task.CompletedTask;
        }
    }

    private static SlskdDownloadItem Grab(string username = "peer") => new(new ReleaseInfo
    {
        Source = $"[{{\"Filename\":{JsonSerializer.Serialize($@"@@{username}\Artist\Album\01.flac")},\"Size\":1000}}]",
        Title = "Artist - Album",
        DownloadUrl = "u"
    })
    {
        Username = username,
        TrackedSinceUtc = GrabbedAt
    };

    private static SlskdEventRecord Event(string username = "peer", string text = Check, DateTime? sentAt = null) => new()
    {
        Id = Guid.NewGuid(),
        Type = "PrivateMessageReceived",
        Data = SlskdPrivateMessageTests.Payload(username, text, (sentAt ?? GrabbedAt.AddMinutes(1)).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"))
    };

    private static SlskdProviderSettings Settings(string baseUrl = "http://slskd-a:5030") =>
        new() { AnswerPeerChallenges = true, BaseUrl = baseUrl };

    private static (SlskdChallengeResponder Responder, MessageRecorder Api, Clock Clock) Build()
    {
        MessageRecorder api = new();
        Clock clock = new();
        return (new SlskdChallengeResponder(api, LogManager.CreateNullLogger(), () => clock.Now, clock.Wait), api, clock);
    }

    [Fact]
    public async Task answers_the_peer_an_unfinished_grab_waits_on_and_marks_it_read()
    {
        (SlskdChallengeResponder responder, MessageRecorder api, _) = Build();

        await responder.HandleAsync(Event(), Settings(), [Grab()]);

        Assert.Equal([("http://slskd-a:5030", "peer", "lantern")], api.Sent);
        Assert.Equal(["peer"], api.Acknowledged);
    }

    [Fact]
    public async Task stays_silent_while_the_setting_is_off()
    {
        (SlskdChallengeResponder responder, MessageRecorder api, _) = Build();
        SlskdProviderSettings settings = Settings();
        settings.AnswerPeerChallenges = false;

        await responder.HandleAsync(Event(), settings, [Grab()]);

        Assert.Empty(api.Sent);
    }

    [Fact]
    public async Task ignores_a_peer_no_grab_waits_on()
    {
        (SlskdChallengeResponder responder, MessageRecorder api, _) = Build();

        await responder.HandleAsync(Event(username: "stranger"), Settings(), [Grab()]);

        Assert.Empty(api.Sent);
    }

    [Fact]
    public async Task ignores_a_peer_whose_grab_was_removed()
    {
        (SlskdChallengeResponder responder, MessageRecorder api, _) = Build();
        SlskdDownloadItem grab = Grab();
        grab.MarkRemoved();

        await responder.HandleAsync(Event(), Settings(), [grab]);

        Assert.Empty(api.Sent);
    }

    [Fact]
    public async Task ignores_a_peer_whose_grab_already_finished()
    {
        (SlskdChallengeResponder responder, MessageRecorder api, _) = Build();
        SlskdDownloadItem grab = Grab();
        grab.SlskdDownloadDirectory = new SlskdDownloadDirectory(@"@@peer\Artist\Album", 1, [SucceededFile()]);

        await responder.HandleAsync(Event(), Settings(), [grab]);

        Assert.Empty(api.Sent);
    }

    private static SlskdDownloadFile SucceededFile() => new(
        Id: "id",
        Username: "peer",
        Direction: "Download",
        Filename: @"@@peer\Artist\Album\01.flac",
        Size: 1000,
        StartOffset: 0,
        State: "Completed, Succeeded",
        RequestedAt: GrabbedAt,
        EnqueuedAt: GrabbedAt,
        StartedAt: GrabbedAt,
        BytesTransferred: 1000,
        AverageSpeed: 0,
        BytesRemaining: 0,
        ElapsedTime: TimeSpan.Zero,
        PercentComplete: 100,
        RemainingTime: TimeSpan.Zero,
        EndedAt: TimeSpan.Zero,
        BatchId: null);

    [Fact]
    public async Task ignores_a_check_sent_before_the_grab()
    {
        (SlskdChallengeResponder responder, MessageRecorder api, _) = Build();

        await responder.HandleAsync(Event(sentAt: GrabbedAt.AddHours(-3)), Settings(), [Grab()]);

        Assert.Empty(api.Sent);
    }

    [Fact]
    public async Task answers_a_peer_once_per_cooldown()
    {
        (SlskdChallengeResponder responder, MessageRecorder api, Clock clock) = Build();

        await responder.HandleAsync(Event(), Settings(), [Grab()]);
        clock.Now += TimeSpan.FromHours(1);
        await responder.HandleAsync(Event(sentAt: clock.Now), Settings(), [Grab()]);
        Assert.Single(api.Sent);

        clock.Now += SlskdChallengeResponder.PeerCooldown;
        await responder.HandleAsync(Event(sentAt: clock.Now), Settings(), [Grab()]);
        Assert.Equal(2, api.Sent.Count);
    }

    [Fact]
    public async Task a_send_slskd_refused_leaves_the_next_check_free_to_answer()
    {
        (SlskdChallengeResponder responder, MessageRecorder api, _) = Build();
        api.SendResult = () => false;
        await responder.HandleAsync(Event(), Settings(), [Grab()]);

        api.SendResult = () => true;
        await responder.HandleAsync(Event(), Settings(), [Grab()]);

        Assert.Equal(2, api.Sent.Count);
        Assert.Equal(["peer"], api.Acknowledged);
    }

    [Fact]
    public async Task a_send_that_throws_is_swallowed_and_leaves_the_next_check_free_to_answer()
    {
        (SlskdChallengeResponder responder, MessageRecorder api, _) = Build();
        api.SendResult = () => throw new HttpRequestException("slskd down");
        await responder.HandleAsync(Event(), Settings(), [Grab()]);

        api.SendResult = () => true;
        await responder.HandleAsync(Event(), Settings(), [Grab()]);

        Assert.Equal(2, api.Sent.Count);
    }

    [Fact]
    public async Task each_slskd_server_answers_for_its_own_account()
    {
        (SlskdChallengeResponder responder, MessageRecorder api, _) = Build();

        await responder.HandleAsync(Event(), Settings("http://slskd-a:5030"), [Grab()]);
        await responder.HandleAsync(Event(), Settings("http://slskd-b:5030"), [Grab()]);
        await responder.HandleAsync(Event(), Settings("http://slskd-a:5030"), [Grab()]);

        Assert.Equal(["http://slskd-a:5030", "http://slskd-b:5030"], api.Sent.Select(s => s.BaseUrl));
    }

    [Fact]
    public async Task spaces_answers_to_different_peers_apart()
    {
        (SlskdChallengeResponder responder, MessageRecorder api, Clock clock) = Build();

        await responder.HandleAsync(Event(username: "peer"), Settings(), [Grab("peer"), Grab("other")]);
        await responder.HandleAsync(Event(username: "other"), Settings(), [Grab("peer"), Grab("other")]);

        Assert.Equal(2, api.Sent.Count);
        Assert.Equal([SlskdChallengeResponder.SendGap], clock.Waits);
    }
}

public class SlskdConversationRequestTests
{
    private static SlskdProviderSettings Settings => new() { BaseUrl = "http://slskd:5030", ApiKey = "k" };

    [Fact]
    public async Task sends_the_answer_as_a_bare_json_string_to_the_peers_conversation()
    {
        FakeHttpClient http = new(request => FakeHttpClient.Respond(request, HttpStatusCode.Created));

        bool sent = await new SlskdApiClient(http, LogManager.CreateNullLogger()).SendPrivateMessageAsync(Settings, "a peer", "quiet \"harbour\"");

        HttpRequest request = Assert.Single(http.Requests);
        Assert.True(sent);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.EndsWith("/api/v0/conversations/a%20peer", request.Url.ToString());
        Assert.Equal("application/json", request.Headers.ContentType);
        Assert.Equal("quiet \"harbour\"", JsonSerializer.Deserialize<string>(Encoding.UTF8.GetString(request.ContentData)));
    }

    [Fact]
    public async Task an_answer_slskd_did_not_send_reports_false()
    {
        // slskd returns 200 without sending to a blacklisted peer; only 201 means sent.
        FakeHttpClient http = new(request => FakeHttpClient.Respond(request, HttpStatusCode.OK));

        Assert.False(await new SlskdApiClient(http, LogManager.CreateNullLogger()).SendPrivateMessageAsync(Settings, "peer", "lantern"));
    }

    [Fact]
    public async Task marks_the_conversation_read_with_a_put_and_no_body()
    {
        FakeHttpClient http = new(request => FakeHttpClient.Respond(request, HttpStatusCode.OK));

        Assert.True(await new SlskdApiClient(http, LogManager.CreateNullLogger()).AcknowledgeConversationAsync(Settings, "peer"));

        HttpRequest request = Assert.Single(http.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.EndsWith("/api/v0/conversations/peer", request.Url.ToString());
        Assert.Null(request.ContentData);
    }
}
