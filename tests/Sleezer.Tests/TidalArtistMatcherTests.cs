using NzbDrone.Plugin.Sleezer.Core.Tidal;
using Xunit;

namespace Sleezer.Tests;

// Covers upstream TrevTV/Lidarr.Plugin.Tidal issue #21: Various Artists,
// Soundtrack and Cast albums silently produce zero hits because the
// strict artist-name match doesn't recognize compilation sentinels.
public class TidalArtistMatcherTests
{
    [Theory]
    [InlineData("Pink Floyd", new[] { "Pink Floyd" })]
    [InlineData("pink floyd", new[] { "Pink Floyd" })]
    [InlineData("Floyd", new[] { "Pink Floyd" })]
    [InlineData("Pink Floyd & David Gilmour", new[] { "Pink Floyd" })]
    public void ArtistMatches_exact_or_substring_passes(string query, string[] albumArtists)
    {
        Assert.True(TidalArtistMatcher.ArtistMatches(query, albumArtists));
    }

    [Theory]
    [InlineData("Anything", new[] { "Various Artists" })]
    [InlineData("Anything", new[] { "Various" })]
    [InlineData("Anything", new[] { "Soundtrack" })]
    [InlineData("Anything", new[] { "Original Cast" })]
    [InlineData("Anything", new[] { "Cast Recording" })]
    [InlineData("Anything", new[] { "Original Motion Picture Soundtrack" })]
    public void ArtistMatches_compilation_marker_on_album_side_wins(string query, string[] albumArtists)
    {
        Assert.True(TidalArtistMatcher.ArtistMatches(query, albumArtists));
    }

    [Theory]
    [InlineData("Various Artists", new[] { "Pink Floyd" })]
    [InlineData("Soundtrack", new[] { "John Williams" })]
    public void ArtistMatches_compilation_marker_on_query_side_wins(string query, string[] albumArtists)
    {
        Assert.True(TidalArtistMatcher.ArtistMatches(query, albumArtists));
    }

    [Theory]
    [InlineData("Pink Floyd", new[] { "Led Zeppelin" })]
    [InlineData("Beatles", new[] { "John Williams", "Yo-Yo Ma" })]
    public void ArtistMatches_unrelated_artists_fail(string query, string[] albumArtists)
    {
        Assert.False(TidalArtistMatcher.ArtistMatches(query, albumArtists));
    }

    [Fact]
    public void ArtistMatches_empty_query_passes()
    {
        Assert.True(TidalArtistMatcher.ArtistMatches("", new[] { "Anything" }));
        Assert.True(TidalArtistMatcher.ArtistMatches("   ", new[] { "Anything" }));
    }

    [Fact]
    public void ArtistMatches_empty_album_artists_fails()
    {
        Assert.False(TidalArtistMatcher.ArtistMatches("Pink Floyd", System.Array.Empty<string>()));
    }
}
