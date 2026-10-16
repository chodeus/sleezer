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
}
