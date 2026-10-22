using System.Net;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using Xunit;

namespace Sleezer.Tests;

// The real client with only HTTP faked. Its cache and circuit breaker are static, so each test asks for ids of its own.
public class MusicBrainzLookupsTests
{
    private const string Primary = "5b11f4ce-a62d-471e-81fc-a69a8278c7da";

    public MusicBrainzLookupsTests()
    {
        MusicBrainzLookups.GuestsBreaker.Reset();
        MusicBrainzLookups.RecordingsBreaker.Reset();
    }

    private static string NewId() => Guid.NewGuid().ToString();

    private static (MusicBrainzLookups Client, FakeHttpClient Http) Client(HttpStatusCode status, string body = "{}")
    {
        FakeHttpClient http = new(request => FakeHttpClient.Respond(request, status, body));
        return (new MusicBrainzLookups(http, LogManager.CreateNullLogger()), http);
    }

    private static string Recording(string id, string title, string length) =>
        $$"""{"id":"{{id}}","title":"{{title}}","length":{{length}},"disambiguation":"","video":false,"first-release-date":"2020-03-06"}""";

    [Fact]
    public void A_recording_is_read_once_with_its_title_and_length()
    {
        string id = NewId();
        (MusicBrainzLookups client, FakeHttpClient http) = Client(HttpStatusCode.OK, Recording(id, "Lantern Song (Moss remix)", "474000"));

        Assert.Equal(new MusicBrainzRecording("Lantern Song (Moss remix)", 474000), client.RecordingOf(id));
        Assert.Equal(new MusicBrainzRecording("Lantern Song (Moss remix)", 474000), client.RecordingOf(id.ToUpperInvariant()));

        HttpRequest request = Assert.Single(http.Requests);
        Assert.Equal($"https://musicbrainz.org/ws/2/recording/{id}?fmt=json", request.Url.FullUri);
        Assert.StartsWith("Sleezer/", request.Headers["User-Agent"]);
        Assert.Equal(TimeSpan.FromSeconds(1.1), request.RateLimit);
        Assert.Equal(TimeSpan.FromSeconds(5), request.RequestTimeout);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("0")]
    public void A_recording_without_a_length_gives_zero(string length)
    {
        string id = NewId();
        (MusicBrainzLookups client, _) = Client(HttpStatusCode.OK, Recording(id, "Lantern Song", length));

        Assert.Equal(new MusicBrainzRecording("Lantern Song", 0), client.RecordingOf(id));
    }

    [Fact]
    public void A_recording_with_no_length_field_gives_zero()
    {
        Assert.Equal(new MusicBrainzRecording("Lantern Song", 0), MusicBrainzRecording.FromJson("""{"id":"x","title":"Lantern Song"}"""));
    }

    [Fact]
    public void A_recording_MusicBrainz_no_longer_has_is_null_and_remembered()
    {
        string id = NewId();
        (MusicBrainzLookups client, FakeHttpClient http) = Client(HttpStatusCode.NotFound);

        Assert.Null(client.RecordingOf(id));
        Assert.Null(client.RecordingOf(id));

        Assert.Single(http.Requests);
    }

    [Fact]
    public void A_failed_lookup_is_asked_again_later_not_remembered()
    {
        string id = NewId();
        (MusicBrainzLookups client, FakeHttpClient http) = Client(HttpStatusCode.ServiceUnavailable);

        Assert.Null(client.RecordingOf(id));
        MusicBrainzLookups.RecordingsBreaker.Reset();
        Assert.Null(client.RecordingOf(id));

        Assert.Equal(2, http.Requests.Count);
    }

    [Fact]
    public void A_failed_recording_lookup_pauses_only_recording_lookups()
    {
        string group = NewId();
        FakeHttpClient http = new(request => request.Url.FullUri.Contains("/recording/")
            ? FakeHttpClient.Respond(request, HttpStatusCode.ServiceUnavailable)
            : FakeHttpClient.Respond(request, HttpStatusCode.OK, Credit(group, NewId())));
        MusicBrainzLookups client = new(http, LogManager.CreateNullLogger());

        Assert.Null(client.RecordingOf(NewId()));
        Assert.Null(client.RecordingOf(NewId()));

        Assert.Equal(["Ora Fenn"], client.GuestsOf(group, Primary));
        Assert.Equal(2, http.Requests.Count);
    }

    [Fact]
    public void A_failed_guest_lookup_pauses_only_guest_lookups()
    {
        string id = NewId();
        FakeHttpClient http = new(request => request.Url.FullUri.Contains("/release-group/")
            ? FakeHttpClient.Respond(request, HttpStatusCode.ServiceUnavailable)
            : FakeHttpClient.Respond(request, HttpStatusCode.OK, Recording(id, "Lantern Song", "200000")));
        MusicBrainzLookups client = new(http, LogManager.CreateNullLogger());

        Assert.Null(client.GuestsOf(NewId(), Primary));
        Assert.Null(client.GuestsOf(NewId(), Primary));

        Assert.Equal(new MusicBrainzRecording("Lantern Song", 200000), client.RecordingOf(id));
        Assert.Equal(2, http.Requests.Count);
    }

    private static string Credit(string group, string guest) =>
        $$$"""{"id":"{{{group}}}","title":"Glass Harbour","artist-credit":[{"name":"Juniper Vale","joinphrase":" & ","artist":{"id":"{{{Primary}}}","name":"Juniper Vale"}},{"name":"Ora Fenn","joinphrase":"","artist":{"id":"{{{guest}}}","name":"Ora Fenn"}}]}""";

    [Fact]
    public void A_release_group_credit_is_read_once_without_the_primary_artist()
    {
        string group = NewId();
        (MusicBrainzLookups client, FakeHttpClient http) = Client(HttpStatusCode.OK, Credit(group, NewId()));

        Assert.Equal(["Ora Fenn"], client.GuestsOf(group, Primary));
        Assert.Equal(["Ora Fenn"], client.GuestsOf(group, Primary));

        HttpRequest request = Assert.Single(http.Requests);
        Assert.Equal($"https://musicbrainz.org/ws/2/release-group/{group}?inc=artist-credits&fmt=json", request.Url.FullUri);
    }

    [Fact]
    public void A_release_group_MusicBrainz_no_longer_has_credits_nobody_and_is_remembered()
    {
        string group = NewId();
        (MusicBrainzLookups client, FakeHttpClient http) = Client(HttpStatusCode.NotFound);

        Assert.Equal([], client.GuestsOf(group, Primary)!);
        Assert.Equal([], client.GuestsOf(group, Primary)!);

        Assert.Single(http.Requests);
    }
}
