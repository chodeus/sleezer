using System.Net;
using NzbDrone.Common.Http;
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
}
