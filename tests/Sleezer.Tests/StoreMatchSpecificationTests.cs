using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Core.DecisionEngine;
using NzbDrone.Plugin.Sleezer.Core.Model;
using Xunit;

namespace Sleezer.Tests;

public class StoreMatchSpecificationTests
{
    private static readonly StoreMatchSpecification Spec = new();

    private static bool Accepted(ReleaseInfo? release) =>
        Spec.IsSatisfiedBy(new RemoteAlbum { Release = release }, null).Accepted;

    [Fact]
    public void A_flagged_store_release_is_rejected_with_its_reason()
    {
        var release = new StoreReleaseInfo { Title = "x", Rejection = "runs 108s vs MusicBrainz 181s" };

        var decision = Spec.IsSatisfiedBy(new RemoteAlbum { Release = release }, null);

        Assert.False(decision.Accepted);
        Assert.Equal("runs 108s vs MusicBrainz 181s", decision.Reason);
    }

    [Fact]
    public void An_unflagged_store_release_is_accepted()
    {
        Assert.True(Accepted(new StoreReleaseInfo { Title = "x" }));
    }

    // An empty reason must not reject — the field is set only when a check actually failed.
    [Fact]
    public void An_empty_reason_is_accepted()
    {
        Assert.True(Accepted(new StoreReleaseInfo { Title = "x", Rejection = string.Empty }));
    }

    // The type is the guarantee: only Sleezer's store parsers build StoreReleaseInfo, so a
    // torrent or Usenet result can never be rejected by this specification.
    [Fact]
    public void A_release_from_another_indexer_is_accepted()
    {
        Assert.True(Accepted(new TorrentInfo { Title = "some.torrent" }));
        Assert.True(Accepted(new ReleaseInfo { Title = "some.nzb" }));
    }

    [Fact]
    public void A_missing_release_is_accepted()
    {
        Assert.True(Accepted(null));
        Assert.True(Spec.IsSatisfiedBy(null, null).Accepted);
    }

    [Fact]
    public void Rejections_are_permanent_so_automatic_search_never_grabs_them()
    {
        Assert.Equal(RejectionType.Permanent, Spec.Type);
    }
}
