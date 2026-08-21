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

        Assert.Equal("http://slskd:5030/browse?user=tactleneckg", SlskdItemsParser.BuildPeerUrl(settings, "tactleneckg"));
    }

    [Fact]
    public void The_browser_reachable_url_wins_over_the_container_one()
    {
        SlskdSettings settings = new() { BaseUrl = "http://slskd:5030", ExternalUrl = "http://10.0.20.11:5030" };

        Assert.Equal("http://10.0.20.11:5030/browse?user=raydeeoo", SlskdItemsParser.BuildPeerUrl(settings, "raydeeoo"));
    }

    [Fact]
    public void A_username_needing_escaping_survives_the_round_trip()
    {
        SlskdSettings settings = new() { BaseUrl = "http://slskd:5030/" };

        Assert.Equal("http://slskd:5030/browse?user=vinyl%20%26%20celluloid", SlskdItemsParser.BuildPeerUrl(settings, "vinyl & celluloid"));
    }

    [Fact]
    public void No_link_without_a_peer_or_settings()
    {
        Assert.Equal("", SlskdItemsParser.BuildPeerUrl(new SlskdSettings { BaseUrl = "http://slskd:5030" }, ""));
        Assert.Equal("", SlskdItemsParser.BuildPeerUrl(null, "tactleneckg"));
    }
}
