using NzbDrone.Plugin.Sleezer.Core.Qobuz;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using Xunit;

namespace Sleezer.Tests;

public class StoreVersionFilterTests
{
    // Versions seen on live Qobuz and Deezer downloads.
    [Theory]
    [InlineData("Album Version (Explicit)")]
    [InlineData("Explicit Album Version")]
    [InlineData("Clean Album Version")]
    [InlineData("Album Version")]
    [InlineData("Remastered 2023")]
    [InlineData("(Remastered 2023)")]
    [InlineData("2011 Remaster")]
    [InlineData("Remastered")]
    [InlineData("Explicit")]
    [InlineData("Digitally Remastered")]
    [InlineData("Remastered (2023)")]
    [InlineData("Remastered [2011]")]
    [InlineData("Remastered - 2023")]
    public void Pure_boilerplate_is_dropped(string version) =>
        Assert.Null(StoreVersionFilter.Meaningful(version));

    [Theory]
    [InlineData("Radio Edit")]
    [InlineData("Extended Mix")]
    [InlineData("Live from Abbey Road")]
    [InlineData("Jamie Anderson Remix")]
    [InlineData("Mix Cut")]
    [InlineData("Single Version")]
    [InlineData("Remastered Live Version")]
    [InlineData("Live in 2023")]
    public void A_version_that_names_a_different_recording_is_kept(string version) =>
        Assert.Equal(version, StoreVersionFilter.Meaningful(version));

    [Theory]
    [InlineData("Single Version / Remastered 2023", "Single Version")]
    [InlineData("7\" Single Version / Remastered 2023", "7\" Single Version")]
    [InlineData("Radio Edit - Remastered 2011", "Radio Edit")]
    [InlineData("(Single Version / Remastered 2023)", "(Single Version)")]
    [InlineData("Radio Edit (Explicit)", "Radio Edit")]
    [InlineData("Radio Edit / Remastered (2011)", "Radio Edit")]
    public void Only_the_boilerplate_part_of_a_mixed_version_is_dropped(string version, string expected) =>
        Assert.Equal(expected, StoreVersionFilter.Meaningful(version));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void No_version_stays_empty(string? version) =>
        Assert.Null(StoreVersionFilter.Meaningful(version));

    [Theory]
    [InlineData("Song", "Album Version", "Song")]
    [InlineData("Song (Album Version)", "Album Version", "Song")]
    [InlineData("Song (Single Version / Remastered 2023)", "Single Version / Remastered 2023", "Song (Single Version)")]
    [InlineData("Song", "Radio Edit", "Song (Radio Edit)")]
    [InlineData("Song (Radio Edit)", "Radio Edit", "Song (Radio Edit)")]
    [InlineData("Song", null, "Song")]
    [InlineData("Live Forever", "Live", "Live Forever (Live)")]
    [InlineData("Song", "(Single Version / Remastered 2023)", "Song (Single Version)")]
    [InlineData("Song (Single Version / Remastered 2023)", "(Single Version / Remastered 2023)", "Song (Single Version)")]
    public void A_title_carries_only_its_meaningful_version(string title, string? version, string expected) =>
        Assert.Equal(expected, StoreVersionFilter.TitleWithVersion(title, version));

    [Fact]
    public void Qobuz_album_artists_keep_only_the_main_ones() =>
        Assert.Equal(["Main Artist"], QobuzAlbumArtists.ForTags(
        [
            ("Main Artist", ["MAIN-ARTIST"]),
            ("Guest Singer", ["featured-artist"])
        ]));

    [Fact]
    public void Qobuz_album_artists_without_roles_are_all_kept() =>
        Assert.Equal(["First", "Second"], QobuzAlbumArtists.ForTags([("First", null), ("Second", null), ("", null)]));

    [Fact]
    public void Qobuz_main_artists_are_empty_without_roles() =>
        Assert.Empty(QobuzAlbumArtists.Main([("First", null), ("Second", ["featured-artist"])]));
}
