using NzbDrone.Core.Parser;
using NzbDrone.Plugin.Sleezer.Core.Download;
using NzbDrone.Plugin.Sleezer.Core.Model;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using Xunit;
using LidarrParser = NzbDrone.Core.Parser.Parser;

namespace Sleezer.Tests;

public class QueueTitleTests
{
    private const string Searched = "Song (Remixer remix)";
    private const string StoreWording = "Song (feat. Guest) [Remixer Remix]";
    private const string StoreQueueTitle = "Some Artist - " + StoreWording + " [WEB] FLAC";

    private static string GrabTitle()
    {
        StoreReleaseInfo release = new() { Artist = "Some Artist", Album = StoreWording, CandidateTitle = StoreWording };
        ReleaseTitle.Compose(release, "Some Artist", StoreWording, " (2026) [FLAC] [WEB]");
        ReleaseTitle.AsSearchedAlbum(release, "Some Artist", Searched);
        return release.Title;
    }

    private static string ParsedAlbum(string title) => LidarrParser.ParseAlbumTitle(title)!.AlbumTitle.CleanArtistName();

    [Fact]
    public void The_store_title_parses_to_the_plain_album()
    {
        Assert.Equal("Song".CleanArtistName(), ParsedAlbum(StoreQueueTitle));
    }

    [Fact]
    public void A_grabbed_item_reports_a_title_that_parses_to_the_grabbed_album()
    {
        Assert.Equal(Searched.CleanArtistName(), ParsedAlbum(QueueTitle.For(GrabTitle(), StoreQueueTitle)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void An_item_saved_without_a_grab_title_keeps_the_store_title(string? releaseTitle)
    {
        Assert.Equal(StoreQueueTitle, QueueTitle.For(releaseTitle, StoreQueueTitle));
    }
}
