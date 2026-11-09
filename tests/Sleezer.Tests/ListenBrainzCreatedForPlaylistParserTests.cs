using System.Net;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Common.Http.Dispatchers;
using NzbDrone.Common.TPL;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Exceptions;
using NzbDrone.Plugin.Sleezer.ImportLists.ListenBrainz.ListenBrainzCreatedForPlaylist;
using Xunit;

namespace Sleezer.Tests;

public class ListenBrainzCreatedForPlaylistParserTests
{
    private const string Listing = """
        {"playlists": [
          {"playlist": {"identifier": "https://listenbrainz.org/playlist/aaa", "title": "A", "extension": {"https://musicbrainz.org/doc/jspf#playlist": {"additional_metadata": {"algorithm_metadata": {"source_patch": "daily-jams"}}}}}},
          {"playlist": {"identifier": "https://listenbrainz.org/playlist/bbb", "title": "B", "extension": {"https://musicbrainz.org/doc/jspf#playlist": {"additional_metadata": {"algorithm_metadata": {"source_patch": "daily-jams"}}}}}}
        ]}
        """;

    private const string OneTrack = """{"playlist": {"track": [{"album": "Album", "creator": "Artist", "title": "Song"}]}}""";
    private const string NoTracks = """{"playlist": {"track": []}}""";

    private static IList<NzbDrone.Core.Parser.Model.ImportListItemInfo> Parse(Func<string, (HttpStatusCode Status, string Body)> playlist)
    {
        ListenBrainzCreatedForPlaylistSettings settings = new();
        FakeHttpClient http = new(request =>
        {
            (HttpStatusCode status, string body) = playlist(request.Url.FullUri);
            return FakeHttpClient.Respond(request, status, body);
        });
        ListenBrainzCreatedForPlaylistParser parser = new(settings, new ListenBrainzCreatedForPlaylistRequestGenerator(settings), http);

        HttpRequest listingRequest = new("https://api.listenbrainz.org/1/user/someone/playlists/createdfor");
        return parser.ParseResponse(new ImportListResponse(new ImportListRequest(listingRequest), FakeHttpClient.Respond(listingRequest, HttpStatusCode.OK, Listing)));
    }

    [Fact]
    public void Every_playlist_failing_fails_the_list()
    {
        Assert.Throws<ImportListException>(() => Parse(_ => (HttpStatusCode.InternalServerError, "{}")));
    }

    [Fact]
    public void One_playlist_failing_keeps_the_others()
    {
        var items = Parse(url => url.EndsWith("/aaa") ? (HttpStatusCode.InternalServerError, "{}") : (HttpStatusCode.OK, OneTrack));

        Assert.Equal("Album", Assert.Single(items).Album);
    }

    [Fact]
    public void Empty_playlists_are_an_empty_list_not_a_failure()
    {
        Assert.Empty(Parse(_ => (HttpStatusCode.OK, NoTracks)));
    }

    private sealed class WireDispatcher(Func<HttpRequest, HttpResponse> handler) : IHttpDispatcher
    {
        public Task<HttpResponse> GetResponseAsync(HttpRequest request, CookieContainer cookies) => Task.FromResult(handler(request));
    }

    // Lidarr's real HttpClient, which throws on an error status; only the wire is faked.
    private static IList<NzbDrone.Core.Parser.Model.ImportListItemInfo> ParseWithRealClient(Func<string, (HttpStatusCode Status, string Body, string? RetryAfter)> playlist)
    {
        Logger nullLogger = LogManager.CreateNullLogger();
        CacheManager cache = new();
        NzbDrone.Common.Http.HttpClient http = new([], cache, new RateLimitService(cache, nullLogger), new WireDispatcher(request =>
        {
            (HttpStatusCode status, string body, string? retryAfter) = playlist(request.Url.FullUri);
            return FakeHttpClient.Respond(request, status, body, retryAfter);
        }), nullLogger);

        ListenBrainzCreatedForPlaylistSettings settings = new();
        ListenBrainzCreatedForPlaylistParser parser = new(settings, new ListenBrainzCreatedForPlaylistRequestGenerator(settings), http);

        HttpRequest listingRequest = new("https://api.listenbrainz.org/1/user/someone/playlists/createdfor");
        return parser.ParseResponse(new ImportListResponse(new ImportListRequest(listingRequest), FakeHttpClient.Respond(listingRequest, HttpStatusCode.OK, Listing)));
    }

    [Fact]
    public void Through_the_real_client_every_playlist_failing_fails_the_list_and_says_why()
    {
        ImportListException ex = Assert.Throws<ImportListException>(() => ParseWithRealClient(_ => (HttpStatusCode.InternalServerError, "{}", null)));

        Assert.Contains("failed to load", ex.Message);
    }

    [Fact]
    public void Through_the_real_client_one_playlist_failing_keeps_the_others()
    {
        var items = ParseWithRealClient(url => url.EndsWith("/aaa") ? (HttpStatusCode.InternalServerError, "{}", null) : (HttpStatusCode.OK, OneTrack, null));

        Assert.Equal("Album", Assert.Single(items).Album);
    }

    [Fact]
    public void A_rate_limit_reaches_lidarr_with_its_retry_after()
    {
        Exception? ex = Record.Exception(() => ParseWithRealClient(_ => ((HttpStatusCode)429, "{}", "3600")));

        TooManyRequestsException limited = Assert.IsType<TooManyRequestsException>(ex);
        Assert.Equal(TimeSpan.FromSeconds(3600), limited.RetryAfter);
    }
}
