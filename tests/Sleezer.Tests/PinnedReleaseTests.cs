using NzbDrone.Core.Datastore;
using NzbDrone.Core.Music;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using Xunit;

namespace Sleezer.Tests;

public class PinnedReleaseTests
{
    private static Album AlbumWith(bool anyReleaseOk, params bool[] monitored) => new()
    {
        AnyReleaseOk = anyReleaseOk,
        AlbumReleases = new LazyLoaded<List<AlbumRelease>>([.. monitored.Select((m, i) => new AlbumRelease { ForeignReleaseId = $"r{i}", Monitored = m })]),
    };

    [Fact]
    public void The_only_monitored_release_is_pinned_when_any_release_ok_is_off()
    {
        Assert.Equal("r1", PinnedRelease.Of(AlbumWith(false, false, true, false))?.ForeignReleaseId);
    }

    [Theory]
    [InlineData(true, new[] { false, true })]
    [InlineData(false, new[] { true, true })]
    [InlineData(false, new[] { false, false })]
    public void Nothing_is_pinned_without_exactly_one_monitored_release_and_any_release_ok_off(bool anyReleaseOk, bool[] monitored)
    {
        Assert.Null(PinnedRelease.Of(AlbumWith(anyReleaseOk, monitored)));
    }

    [Fact]
    public void An_album_without_releases_or_no_album_has_no_pin()
    {
        Assert.Null(PinnedRelease.Of(new Album { AnyReleaseOk = false }));
        Assert.Null(PinnedRelease.Of(null));
    }

    private static AlbumRelease Release(string title, params string[] tracks) => new()
    {
        Title = title,
        Tracks = new LazyLoaded<List<Track>>([.. tracks.Select(t => new Track { Title = t })]),
    };

    [Theory]
    [InlineData("Freaks", "Freaks (Radio Edit)")]
    [InlineData("Freaks", "Freaks (feat. Savage) [Radio Edit]")]
    public void A_single_whose_only_track_is_the_title_plus_a_qualifier_calls_for_the_track(string release, string track)
    {
        Assert.Equal(track, PinnedRelease.TitleOf(Release(release, track)));
    }

    [Theory]
    [InlineData("Freaks", new[] { "Freaks" })]
    [InlineData("Freaks", new[] { "Freaks (feat. Savage)" })]
    [InlineData("Summer EP", new[] { "Another Song (Radio Edit)" })]
    [InlineData("Freaks", new[] { "Freaks (Radio Edit)", "Freaks (Extended Mix)" })]
    [InlineData("Freaks", new string[0])]
    public void Any_other_release_calls_for_its_own_title(string release, string[] tracks)
    {
        Assert.Equal(release, PinnedRelease.TitleOf(Release(release, tracks)));
    }

    [Fact]
    public void A_release_without_loaded_tracks_calls_for_its_own_title()
    {
        Assert.Equal("Freaks", PinnedRelease.TitleOf(new AlbumRelease { Title = "Freaks" }));
        Assert.Null(PinnedRelease.TitleOf(null));
    }
}
