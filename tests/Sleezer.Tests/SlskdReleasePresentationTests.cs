using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Core.Model;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek;
using Xunit;

namespace Sleezer.Tests;

// The title appends every ExtraInfo entry and then the source tag, so a CD rip
// whose edition is also "CD" rendered as "... [CD] [CD]".
public class AlbumTitleTagTests
{
    private static string TitleOf(string sourceTag, params string[] extraInfo)
    {
        AlbumData album = new("Slskd", "SoulseekDownloadProtocol")
        {
            ArtistName = "Muse",
            AlbumName = "The Resistance",
            Codec = AudioFormat.MP3,
            Bitrate = 320,
            SourceTag = sourceTag,
            ExtraInfo = [.. extraInfo]
        };

        return album.ToReleaseInfo().Title;
    }

    [Fact]
    public void An_edition_matching_the_source_tag_is_not_repeated()
    {
        Assert.Equal("Muse - The Resistance [MP3 320kbps] [CD]", TitleOf("CD", "CD"));
    }

    [Fact]
    public void The_match_ignores_case()
    {
        Assert.Equal("Muse - The Resistance [MP3 320kbps] [WEB]", TitleOf("WEB", "web"));
    }

    [Fact]
    public void A_distinct_edition_is_still_shown()
    {
        Assert.Equal("Muse - The Resistance [MP3 320kbps] [DELUXE] [WEB]", TitleOf("WEB", "DELUXE"));
    }

    [Fact]
    public void Repeated_editions_collapse_to_one()
    {
        Assert.Equal("Muse - The Resistance [MP3 320kbps] [DELUXE] [WEB]", TitleOf("WEB", "DELUXE", "deluxe"));
    }

    [Fact]
    public void A_release_without_an_edition_is_unchanged()
    {
        Assert.Equal("Muse - The Resistance [MP3 320kbps] [WEB]", TitleOf("WEB"));
    }
}

// Lidarr renders InfoUrl as the release title's href and has no column for the
// peer, so the link is the only place the username can surface in the grid.
public class SlskdPeerLinkTests
{
    [Fact]
    public void The_link_points_at_the_peers_browse_page()
    {
        SlskdSettings settings = new() { BaseUrl = "http://slskd:5030" };

        Assert.Equal("http://slskd:5030/browse?user=tactleneckg", SlskdUrls.Peer(settings, "tactleneckg"));
    }

    [Fact]
    public void The_browser_reachable_url_wins_over_the_container_one()
    {
        SlskdSettings settings = new() { BaseUrl = "http://slskd:5030", ExternalUrl = "http://10.0.20.11:5030" };

        Assert.Equal("http://10.0.20.11:5030/browse?user=raydeeoo", SlskdUrls.Peer(settings, "raydeeoo"));
    }

    [Fact]
    public void A_username_needing_escaping_survives_the_round_trip()
    {
        SlskdSettings settings = new() { BaseUrl = "http://slskd:5030/" };

        Assert.Equal("http://slskd:5030/browse?user=vinyl%20%26%20celluloid", SlskdUrls.Peer(settings, "vinyl & celluloid"));
    }

    [Fact]
    public void No_link_without_a_peer_or_settings()
    {
        Assert.Equal("", SlskdUrls.Peer(new SlskdSettings { BaseUrl = "http://slskd:5030" }, ""));
        Assert.Equal("", SlskdUrls.Peer(null, "tactleneckg"));
    }

    // A relative URL would resolve against Lidarr's own address, not slskd's.
    [Fact]
    public void No_link_without_a_configured_host()
    {
        Assert.Equal("", SlskdUrls.Peer(new SlskdSettings { BaseUrl = "", ExternalUrl = "" }, "tactleneckg"));
        Assert.Equal("", SlskdUrls.Search(new SlskdSettings { BaseUrl = "", ExternalUrl = "" }, "search-1"));
    }
}

// The peer link is display; the search a release came from is identity. Grab
// cleanup matches on the identity, so it must not be read off the display link.
public class SlskdSearchIdentityTests
{
    private static readonly SlskdSettings Settings = new() { BaseUrl = "http://slskd:5030" };

    [Fact]
    public void A_release_carries_the_search_it_came_from()
    {
        Assert.Equal("http://slskd:5030/searches/abc-123", SlskdUrls.Search(Settings, "abc-123"));
        Assert.True(SlskdUrls.IsFromSearch(SlskdUrls.Search(Settings, "abc-123"), "abc-123"));
    }

    // The regression: cleanup used to match the display link, so pointing that
    // at the peer silently stopped interactive searches being removed.
    [Fact]
    public void A_peer_link_never_identifies_a_search()
    {
        Assert.False(SlskdUrls.IsFromSearch(SlskdUrls.Peer(Settings, "tactleneckg"), "abc-123"));
    }

    [Fact]
    public void Another_searchs_url_does_not_match()
    {
        Assert.False(SlskdUrls.IsFromSearch(SlskdUrls.Search(Settings, "abc-123"), "def-456"));
    }

    [Fact]
    public void A_missing_url_or_id_never_matches()
    {
        Assert.False(SlskdUrls.IsFromSearch(null, "abc-123"));
        Assert.False(SlskdUrls.IsFromSearch("", "abc-123"));
        Assert.False(SlskdUrls.IsFromSearch("http://slskd:5030/searches/abc-123", ""));
    }

    // The identity has to survive onto the release, or grab cleanup has nothing
    // to match: this is what broke when the display link stopped carrying it.
    [Fact]
    public void The_identity_reaches_the_release()
    {
        AlbumData album = new("Slskd", "SoulseekDownloadProtocol")
        {
            ArtistName = "Muse",
            AlbumName = "The Resistance",
            InfoUrl = SlskdUrls.Peer(Settings, "tactleneckg"),
            CommentUrl = SlskdUrls.Search(Settings, "abc-123")
        };

        ReleaseInfo release = album.ToReleaseInfo();

        Assert.True(SlskdUrls.IsFromSearch(release.CommentUrl, "abc-123"));
        Assert.Equal("http://slskd:5030/browse?user=tactleneckg", release.InfoUrl);
    }
}
