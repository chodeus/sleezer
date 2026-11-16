using NzbDrone.Core.ImportLists.Qobuz;
using NzbDrone.Core.Parser.Model;
using QobuzApiSharp.Models.Content;
using QobuzApiSharp.Service;
using Xunit;

namespace Sleezer.Tests;

public class QobuzPlaylistTrackTests
{
    // A playlist/get page with extra=tracks, read through the parser GetPlaylist uses.
    private const string PlaylistPage = """
        {
          "id": 5001,
          "name": "Test playlist",
          "tracks": {
            "offset": 0,
            "limit": 50,
            "total": 6,
            "items": [
              { "id": 1, "title": "Own Song", "performer": { "id": 11, "name": "Ada Example" },
                "album": { "id": "al1", "title": "First Light", "version": "Deluxe", "artist": { "id": 11, "name": "Ada Example" } } },
              { "id": 2, "title": "Compiled Song", "performer": { "id": 12, "name": "Ben Sample" },
                "album": { "id": "al2", "title": "Summer Mix", "version": null, "artist": { "id": 900, "name": "Various Artists" } } },
              { "id": 3, "title": "Loose Song", "performer": { "id": 13, "name": "Cara Test" },
                "album": { "id": "al3", "title": "Night Drive", "version": null } },
              { "id": 4, "title": "Short Credit", "performer": { "id": 14, "name": "VA" },
                "album": { "id": "al4", "title": "Hits", "version": null } },
              { "id": 5, "title": "No Album", "performer": { "id": 15, "name": "Dan Placeholder" } },
              { "id": 6, "title": "Chanson", "performer": { "id": 16, "name": "Eve Exemple" },
                "album": { "id": "al6", "title": "Été Mix", "version": null, "artist": { "id": 901, "name": "Multi-interprètes" } } }
            ]
          }
        }
        """;

    [Fact]
    public void A_track_from_its_artists_own_album_sends_that_artist_and_album()
    {
        var item = ItemFor(1);

        Assert.NotNull(item);
        Assert.Equal("Ada Example", item.Artist);
        Assert.Equal("First Light (Deluxe)", item.Album);
    }

    [Fact]
    public void A_track_from_a_Various_Artists_compilation_sends_nothing()
        => Assert.Null(ItemFor(2));

    [Fact]
    public void A_track_from_a_Multi_interpretes_compilation_sends_nothing()
        => Assert.Null(ItemFor(6));

    [Fact]
    public void A_track_whose_album_names_no_artist_sends_its_performer()
    {
        var item = ItemFor(3);

        Assert.NotNull(item);
        Assert.Equal("Cara Test", item.Artist);
        Assert.Equal("Night Drive", item.Album);
    }

    [Fact]
    public void A_track_whose_only_credit_is_VA_sends_nothing()
        => Assert.Null(ItemFor(4));

    [Fact]
    public void A_track_with_no_album_sends_nothing()
        => Assert.Null(ItemFor(5));

    private static ImportListItemInfo? ItemFor(int trackId)
    {
        using var response = new HttpResponseMessage { Content = new StringContent(PlaylistPage) };
        var playlist = QobuzApiHelper.DeserializeResponse<Playlist>(response);
        return QobuzPlaylistTrack.ToListItem(playlist.Tracks.Items.Single(t => t.Id == trackId));
    }
}
