using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Blocklisting;
using Xunit;

namespace Sleezer.Tests;

// A store album is offered once per quality tier, and the tier is the last segment of its Guid.
public class StoreAlbumBlocklistTests
{
    private const string ManualRemoval = "Manually marked as failed";
    private const string DownloadFailure = "Failed download detected";

    public static TheoryData<string, string, string> TwoTiersOfOneAlbum => new()
    {
        { "qobuz", "1_Qobuz-abc123xyz-FLACHiRes24Bit96kHz", "1_Qobuz-abc123xyz-FLACLossless" },
        { "deezer", "2_Deezer-123456789-9", "2_Deezer-123456789-3" },
        { "tidal", "3_Tidal-987654321-HI_RES_LOSSLESS", "3_Tidal-987654321-LOSSLESS" },
    };

    private static IBlocklistForProtocol Blocklist(string store, FakeBlocklistRepository repo) => store switch
    {
        "qobuz" => new QobuzBlocklist(repo),
        "deezer" => new DeezerBlocklist(repo),
        "tidal" => new TidalBlocklist(repo),
        _ => throw new ArgumentOutOfRangeException(nameof(store), store, "Unknown store"),
    };

    private static Blocklist Row(string guid, string message) =>
        new() { ArtistId = 1, TorrentInfoHash = guid, Message = message, Date = DateTime.UtcNow };

    [Theory]
    [MemberData(nameof(TwoTiersOfOneAlbum))]
    public void A_hand_removed_tier_blocks_every_tier_of_that_album(string store, string removed, string otherTier)
    {
        FakeBlocklistRepository repo = new();
        repo.Add(Row(removed, ManualRemoval));
        IBlocklistForProtocol blocklist = Blocklist(store, repo);

        Assert.True(blocklist.IsBlocklisted(1, new ReleaseInfo { Guid = removed }));
        Assert.True(blocklist.IsBlocklisted(1, new ReleaseInfo { Guid = otherTier }));
    }

    [Theory]
    [MemberData(nameof(TwoTiersOfOneAlbum))]
    public void A_failed_download_blocks_only_its_own_tier(string store, string failed, string otherTier)
    {
        FakeBlocklistRepository repo = new();
        repo.Add(Row(failed, DownloadFailure));
        IBlocklistForProtocol blocklist = Blocklist(store, repo);

        Assert.True(blocklist.IsBlocklisted(1, new ReleaseInfo { Guid = failed }));
        Assert.False(blocklist.IsBlocklisted(1, new ReleaseInfo { Guid = otherTier }));
    }

    [Theory]
    [InlineData("deezer", "2_Deezer-12345670-9", "2_Deezer-1234567-9")]
    [InlineData("qobuz", "14_Qobuz-abc123xyz-FLACLossless", "4_Qobuz-abc123xyz-FLACLossless")]
    public void A_hand_removal_leaves_an_album_whose_key_is_part_of_the_removed_guid(string store, string removed, string candidate)
    {
        FakeBlocklistRepository repo = new();
        repo.Add(Row(removed, ManualRemoval));

        Assert.False(Blocklist(store, repo).IsBlocklisted(1, new ReleaseInfo { Guid = candidate }));
    }

    [Fact]
    public void A_hand_removal_leaves_the_same_album_on_another_indexer()
    {
        FakeBlocklistRepository repo = new();
        repo.Add(Row("1_Qobuz-abc123xyz-FLACLossless", ManualRemoval));

        Assert.False(new QobuzBlocklist(repo).IsBlocklisted(1, new ReleaseInfo { Guid = "8_Qobuz-abc123xyz-FLACHiRes24Bit96kHz" }));
    }

    [Fact]
    public void A_hand_removed_soulseek_folder_leaves_the_same_peers_other_folders()
    {
        FakeBlocklistRepository repo = new();
        repo.Add(Row("4_Slskd-peer-1a2b3c", ManualRemoval));
        SoulseekBlocklist blocklist = new(repo);

        Assert.True(blocklist.IsBlocklisted(1, new ReleaseInfo { Guid = "4_Slskd-peer-1a2b3c" }));
        Assert.False(blocklist.IsBlocklisted(1, new ReleaseInfo { Guid = "4_Slskd-peer-4d5e6f" }));
    }
}
