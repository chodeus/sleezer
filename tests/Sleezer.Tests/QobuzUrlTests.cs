using NzbDrone.Plugin.Sleezer.Core.Qobuz;
using Xunit;

namespace Sleezer.Tests;

public class QobuzUrlTests
{
    [Theory]
    // Storefront album link — the shape the indexer actually emits.
    [InlineData("https://www.qobuz.com/au-en/album/in-rainbows-radiohead/0060254706267", "0060254706267")]
    [InlineData("https://www.qobuz.com/fr-fr/album/some-slug/abc123xyz", "abc123xyz")]
    // Player link.
    [InlineData("https://open.qobuz.com/album/0060254706267", "0060254706267")]
    // Purchase link carries an extra segment before the id.
    [InlineData("https://www.qobuz.com/au-en/album/some-slug/download-streaming-albums/xyz789", "xyz789")]
    // Trailing slash and query string are both stripped.
    [InlineData("https://www.qobuz.com/au-en/album/some-slug/abc123/", "abc123")]
    [InlineData("https://www.qobuz.com/au-en/album/some-slug/abc123?utm_source=x", "abc123")]
    public void TryParse_extracts_album_id(string url, string expectedId)
    {
        Assert.True(QobuzURL.TryParse(url, out QobuzURL? parsed));
        Assert.Equal(QobuzEntityType.Album, parsed!.EntityType);
        Assert.Equal(expectedId, parsed.Id);
    }

    [Theory]
    [InlineData("https://www.qobuz.com/au-en/interpreter/radiohead/12345", QobuzEntityType.Artist)]
    [InlineData("https://open.qobuz.com/artist/12345", QobuzEntityType.Artist)]
    [InlineData("https://open.qobuz.com/track/98765", QobuzEntityType.Track)]
    [InlineData("https://open.qobuz.com/playlist/555", QobuzEntityType.Playlist)]
    [InlineData("https://www.qobuz.com/au-en/label/some-label/777", QobuzEntityType.Label)]
    public void TryParse_maps_entity_types(string url, QobuzEntityType expected)
    {
        Assert.True(QobuzURL.TryParse(url, out QobuzURL? parsed));
        Assert.Equal(expected, parsed!.EntityType);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("https://www.deezer.com/album/12345")]
    [InlineData("https://tidal.com/browse/album/12345")]
    [InlineData("not a url at all")]
    // No entity segment to identify.
    [InlineData("https://www.qobuz.com/au-en/")]
    public void TryParse_rejects_non_qobuz_and_malformed(string? url)
    {
        Assert.False(QobuzURL.TryParse(url!, out QobuzURL? parsed));
        Assert.Null(parsed);
    }

    // Enum.TryParse would happily read "/2/" as QobuzEntityType.Album; the name-keyed
    // lookup must not.
    [Theory]
    [InlineData("https://open.qobuz.com/2/12345")]
    [InlineData("https://open.qobuz.com/0/12345")]
    public void TryParse_rejects_numeric_entity_segment(string url)
    {
        Assert.False(QobuzURL.TryParse(url, out QobuzURL? parsed));
        Assert.Null(parsed);
    }

    [Fact]
    public void TryParse_keeps_the_query_stripped_url()
    {
        Assert.True(QobuzURL.TryParse("https://open.qobuz.com/album/abc?ref=x", out QobuzURL? parsed));
        Assert.Equal("https://open.qobuz.com/album/abc", parsed!.Url);
    }
}
