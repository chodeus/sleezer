using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Core.Model;
using Xunit;

namespace Sleezer.Tests;

// Lidarr copies a grab's indexer flags onto the imported file; a custom format on Internal ranks store grabs.
public class StoreReleaseFlagTests
{
    [Fact]
    public void Store_release_is_flagged_internal()
    {
        StoreReleaseInfo release = new() { Title = "Artist - Album (2019) [FLAC] [WEB]" };

        Assert.Equal(IndexerFlags.Internal, release.IndexerFlags);
    }

    [Fact]
    public void SubSonic_release_has_no_flags()
    {
        ReleaseInfo release = Album("SubSonic", "SubSonicDownloadProtocol").ToReleaseInfo();

        Assert.IsType<StoreReleaseInfo>(release);
        Assert.Equal((IndexerFlags)0, release.IndexerFlags);
    }

    [Fact]
    public void Soulseek_release_has_no_flags()
    {
        ReleaseInfo release = Album("Slskd", "SoulseekDownloadProtocol").ToShareInfo();

        Assert.Equal((IndexerFlags)0, release.IndexerFlags);
    }

    private static AlbumData Album(string indexer, string protocol) => new(indexer, protocol)
    {
        ArtistName = "Artist",
        AlbumName = "Album",
        AlbumId = "id",
    };
}
