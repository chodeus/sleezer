using NzbDrone.Plugin.Sleezer.Core.Utilities;
using Xunit;

namespace Sleezer.Tests;

public class DownloadFoldersTests
{
    private static readonly char Sep = Path.DirectorySeparatorChar;

    [Theory]
    [InlineData("Artist A/Same Title/")]
    [InlineData("Artist A/Same Title")]
    [InlineData("Artist A\\Same Title\\")]
    public void The_store_id_goes_on_the_album_folder(string albumDirectory) =>
        Assert.EndsWith($"Same Title [111]{Sep}", DownloadFolders.WithStoreId(albumDirectory, "111"));

    [Fact]
    public void Two_releases_with_one_title_get_separate_folders() =>
        Assert.NotEqual(
            DownloadFolders.WithStoreId("Artist A/Same Title/", "111"),
            DownloadFolders.WithStoreId("Artist A/Same Title/", "222"));

    [Fact]
    public void The_same_release_downloaded_again_reuses_its_folder() =>
        Assert.Equal(
            DownloadFolders.WithStoreId("Artist A/Same Title/", "111"),
            DownloadFolders.WithStoreId("Artist A/Same Title/", "111"));
}
