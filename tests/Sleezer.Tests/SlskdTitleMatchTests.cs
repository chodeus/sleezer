using NLog;
using NzbDrone.Plugin.Sleezer.Core.Model;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek;
using Xunit;

namespace Sleezer.Tests;

// A search for a one-word single matched a different song whose title merely contained it,
// and that one file counted as full track evidence for the album folder it sat in.
public class SlskdTitleMatchTests
{
    private static readonly SlskdItemsParser Parser = new(LogManager.GetLogger("tests"));

    private static bool StandsAlone(string fileTitle, string wantedTitle) =>
        SlskdTitleMatch.WordsAfter(SlskdTitleMatch.Tokens(fileTitle), SlskdTitleMatch.TitleWords(wantedTitle)) >= 0;

    [Theory]
    [InlineData("Moonward - Tide", "Tide")]
    [InlineData("02 Tide", "Tide")]
    [InlineData("Lantern Song feat. Ivy Rhodes", "Lantern Song")]
    [InlineData("Lantern Song ft Ivy Rhodes", "Lantern Song")]
    [InlineData("Moonward - Harbour Sessions - Lantern Song - Extended Mix", "Lantern Song (extended mix)")]
    [InlineData("Lantern Song (Instrumental Version)", "Lantern Song (instrumental)")]
    [InlineData("Lantern Song extended mix", "Lantern Song (extended mix)")]
    [InlineData("moonward-lantern_song_(harbour_mix)", "Lantern Song (Harbour mix)")]
    [InlineData("07_Moonward_Lantern Song (Harbour mix)", "Lantern Song (Harbour mix)")]
    [InlineData("S.T.A.R. (Night Version)", "S.T.A.R. (Night version)")]
    [InlineData("Lantern Song (A-Side mix)", "Lantern Song (A-Side mix)")]
    [InlineData("02 - The (Lantern) Song", "The (Lantern) Song")]
    [InlineData("Lantern Song.FLAC.1036 kbps", "Lantern Song")]
    [InlineData("Lantern Song 2021 Remaster", "Lantern Song")]
    [InlineData("With", "With")]
    [InlineData("Moonward - With", "With")]
    [InlineData("Moonward - With 2021 Remaster", "With")]
    [InlineData("Salt and Pepper", "Salt & Pepper")]
    [InlineData("Moonward - The Tide", "Tide")]
    [InlineData("Moonward - A$AP Harbour", "A$AP Harbour")]
    [InlineData("Moonward - Smoke With", "Smoke With")]
    [InlineData("Lantern Song\uFF08Single Version\uFF09", "Lantern Song")]
    [InlineData("Lantern Song\u3010Single Version\u3011", "Lantern Song")]
    [InlineData("Lantern Song ~Single Version~", "Lantern Song")]
    [InlineData("Moonward \u00B7 Lantern Song", "Lantern Song")]
    [InlineData("Cafe\u0301 Tide", "Caf\u00E9 Tide")]
    [InlineData("Caf\u00E9 Tide", "Cafe\u0301 Tide")]
    [InlineData("Lantern\u00B4s Song", "Lantern's Song")]
    public void a_title_between_separators_matches(string fileTitle, string wantedTitle)
    {
        Assert.True(StandsAlone(fileTitle, wantedTitle));
    }

    [Theory]
    [InlineData("Moonward - Glass Harbour - Low Tide Rising", "Tide")]
    [InlineData("Tidewater", "Tide")]
    [InlineData("Rising Tide", "Tide")]
    [InlineData("Tide of Ages", "Tide")]
    [InlineData("moonward-lantern_song", "Song")]
    [InlineData("Lantern Song", "Lantern Song (extended mix)")]
    [InlineData("Moonward - Down With It", "With")]
    [InlineData("Moonward - Glass Harbour\u2026 Tide - 05 - Salt Road", "Tide")]
    public void a_title_inside_a_longer_title_or_missing_its_qualifier_does_not_match(string fileTitle, string wantedTitle)
    {
        Assert.False(StandsAlone(fileTitle, wantedTitle));
    }

    [Fact]
    public void a_single_whose_title_is_inside_another_songs_title_does_not_match_the_album_folder()
    {
        Assert.False(SingleMatches("11 - Moonward - Glass Harbour - Low Tide Rising.flac"));
    }

    [Fact]
    public void the_single_itself_in_an_album_folder_still_matches()
    {
        Assert.True(SingleMatches("03 - Moonward - Glass Harbour - Tide.flac"));
    }

    [Fact]
    public void a_single_titled_only_with_a_stop_word_matches_its_own_file()
    {
        const string folder = @"Music\Moonward\2001 - With";
        AlbumData album = Build(folder, [File(folder, "01 - With", 200)], "With", ["With"], "Single", new SlskdSettings());

        Assert.True(album.MatchedSearchCriteria);
    }

    [Fact]
    public void a_stop_word_single_matches_its_file_in_an_album_folder_named_with_the_artist()
    {
        const string folder = @"Music\Moonward\2001 - Glass Harbour";
        AlbumData album = Build(folder, [File(folder, "02 - Moonward - Salt Road", 200), File(folder, "03 - Moonward - With", 200)],
            "With", ["With"], "Single", new SlskdSettings());

        Assert.True(album.MatchedSearchCriteria);
        Assert.Contains("03 - Moonward - With.flac", album.CustomString);
    }

    [Fact]
    public void match_release_tracks_finds_a_stop_word_title()
    {
        const string folder = @"Music\Moonward\2001 - Glass Harbour";
        AlbumData album = Build(folder, [File(folder, "01 - Lantern Song", 130), File(folder, "02 - With", 300)],
            "Glass Harbour", ["Lantern Song", "With"], "Album", new SlskdSettings { MatchReleaseTracks = true }, [130_000, 300_000]);

        Assert.True(album.MatchedSearchCriteria);
    }

    [Fact]
    public void a_single_takes_its_own_file_over_an_earlier_reprise()
    {
        const string folder = @"Music\Moonward\2001 - Glass Harbour";
        List<SlskdFileData> files =
        [
            File(folder, "01 - Salt Road", 200), File(folder, "04 - Tide (Reprise)", 90), File(folder, "07 - Tide", 200), File(folder, "09 - Undertow", 200),
        ];
        AlbumData album = Build(folder, files, "Tide", ["Tide"], "Single", new SlskdSettings());

        Assert.True(album.MatchedSearchCriteria);
        Assert.Contains("07 - Tide.flac", album.CustomString);
        Assert.DoesNotContain("Reprise", album.CustomString);
    }

    [Fact]
    public void a_single_named_like_its_album_takes_the_title_track()
    {
        const string folder = @"Music\Moonward\2001 - Glass Harbour";
        List<SlskdFileData> files =
        [
            File(folder, "Moonward - Glass Harbour - 01 - Salt Road", 200), File(folder, "Moonward - Glass Harbour - 04 - Glass Harbour", 200),
        ];
        AlbumData album = Build(folder, files, "Glass Harbour", ["Glass Harbour"], "Single", new SlskdSettings());

        Assert.Contains("04 - Glass Harbour.flac", album.CustomString);
        Assert.DoesNotContain("Salt Road", album.CustomString);
    }

    [Fact]
    public void when_no_file_ends_with_the_title_the_first_one_is_kept()
    {
        const string folder = @"Music\Moonward\2001 - Glass Harbour";
        List<SlskdFileData> files = [File(folder, "01 - Tide (Album Version)", 200), File(folder, "02 - Tide (Edited)", 200)];
        AlbumData album = Build(folder, files, "Tide", ["Tide"], "Single", new SlskdSettings());

        Assert.Contains("01 - Tide (Album Version).flac", album.CustomString);
        Assert.DoesNotContain("Edited", album.CustomString);
    }

    [Fact]
    public void of_two_files_the_title_ends_the_first_is_kept()
    {
        const string folder = @"Music\Moonward\2001 - Glass Harbour";
        List<SlskdFileData> files = [File(folder, "03 - Tide", 200), File(folder, "11 - Moonward - Tide", 200)];
        AlbumData album = Build(folder, files, "Tide", ["Tide"], "Single", new SlskdSettings());

        Assert.Contains("03 - Tide.flac", album.CustomString);
        Assert.DoesNotContain("11 - Moonward", album.CustomString);
    }

    private static bool SingleMatches(string fileName)
    {
        const string folder = @"Music\Moonward\1999 - Glass Harbour";
        List<SlskdFileData> files =
        [
            new($@"{folder}\{fileName}", null, 16, 30_000_000, 160, ".flac", 44100, 0, false),
            new($@"{folder}\02 - Moonward - Glass Harbour - Salt Road.flac", null, 16, 30_000_000, 250, ".flac", 44100, 0, false),
        ];
        AlbumData album = Parser.CreateAlbumData(
            files.GroupBy(f => SlskdTextProcessor.GetMergedDirectoryKey(f.Filename)).Single(),
            new SlskdSearchData("Moonward", "Tide", false, false, 1, null, TrackCount: 1, Tracks: ["Tide"], AlbumType: "Single"),
            new SlskdFolderData(folder, "Moonward", "Glass Harbour", "1999", "peer", true, 1_000_000, 0, [], 0, 0, 0, [], 0),
            new SlskdSettings(),
            expectedTrackCount: 1);
        return album.MatchedSearchCriteria;
    }

    private static SlskdFileData File(string folder, string name, int seconds) =>
        new($@"{folder}\{name}.flac", null, 16, 30_000_000, seconds, ".flac", 44100, 0, false);

    private static AlbumData Build(string folder, List<SlskdFileData> files, string album, List<string> tracks, string albumType,
        SlskdSettings settings, List<int>? durations = null) =>
        Parser.CreateAlbumData(
            files.GroupBy(f => SlskdTextProcessor.GetMergedDirectoryKey(f.Filename)).Single(),
            new SlskdSearchData("Moonward", album, false, false, 1, null, TrackCount: tracks.Count, Tracks: tracks, AlbumType: albumType, TrackDurations: durations),
            new SlskdFolderData(folder, "Moonward", album, "2001", "peer", true, 1_000_000, 0, [], 0, 0, files.Count, [], 0),
            settings,
            expectedTrackCount: tracks.Count);
}
