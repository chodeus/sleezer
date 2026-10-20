using NLog;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek;
using Xunit;

namespace Sleezer.Tests;

// SlskdItemsParser.JudgeFolder: the verdict and the reasons it gives.
public class SlskdFolderVerdictTests
{
    private static readonly SlskdItemsParser Parser = new(LogManager.GetLogger("tests"));

    private static SlskdFolderVerdict Judge(string folder, string[] fileNames, SlskdSearchData search, SlskdSettings? settings = null, int expected = 0)
    {
        List<SlskdFileData> files = [.. fileNames.Select((n, i) => new SlskdFileData($@"{folder}\{n}", null, 16, 30_000_000 + i, 210 + i, ".flac", 44100, 0, false))];
        return Parser.JudgeFolder(files.GroupBy(f => SlskdTextProcessor.GetMergedDirectoryKey(f.Filename)).Single(), search, settings, expected);
    }

    [Fact]
    public void a_folder_naming_the_artist_and_album_matches_and_says_so()
    {
        SlskdFolderVerdict v = Judge(@"Music\Moonward\Glass Harbour", ["01 Lantern Song.flac"], new SlskdSearchData("Moonward", "Glass Harbour", false, false, 1, null));

        Assert.True(v.Matched);
        Assert.Equal(["artist named", "album named"], v.Reasons);
    }

    [Fact]
    public void a_folder_naming_neither_the_artist_nor_the_album_says_both_checks_failed()
    {
        SlskdFolderVerdict v = Judge(@"Music\Salt Road\Driftwood", ["01 Lantern Song.flac"], new SlskdSearchData("Moonward", "Glass Harbour", false, false, 1, null));

        Assert.False(v.Matched);
        Assert.Equal(["artist not named", "album not named", "artist and album not named together"], v.Reasons);
    }

    // A marks-only album ("÷") normalizes to nothing and used to turn into an artist-only search.
    [Theory]
    [InlineData(@"Music\Moonward\Moonward - ÷ (2017)", true)]
    [InlineData(@"Music\Moonward\Moonward - x (2014)", false)]
    [InlineData(@"Music\Moonward ÷ Tide\Glass Harbour", false)]
    [InlineData(@"Music\Moonward\Glass÷Harbour", false)]
    public void a_marks_only_album_needs_its_marks_in_the_album_folder(string folder, bool matched)
    {
        SlskdFolderVerdict v = Judge(folder, ["01 Lantern Song.flac"], new SlskdSearchData("Moonward", "÷", false, false, 1, null));

        Assert.Equal(matched, v.Matched);
        Assert.Contains(matched ? "album named" : "album not named", v.Reasons);
    }

    [Fact]
    public void spaced_marks_match_the_same_marks_in_the_folder()
    {
        SlskdFolderVerdict v = Judge(@"Music\Moonward\Moonward - ✞ ✦", ["01 Lantern Song.flac"], new SlskdSearchData("Moonward", "✞ ✦", false, false, 1, null));

        Assert.Contains("album named", v.Reasons);
    }

    // With no artist to name, a parent folder's "+" would otherwise pass any album beneath it.
    [Fact]
    public void marks_in_a_parent_folder_do_not_name_the_album()
    {
        SlskdFolderVerdict v = Judge(@"Music\Moon+Ward\Driftwood", ["01 Lantern Song.flac"], new SlskdSearchData(null, "+", false, false, 1, null));

        Assert.False(v.Matched);
        Assert.Contains("album not named", v.Reasons);
    }

    [Fact]
    public void a_different_volume_says_the_volume_differs()
    {
        SlskdFolderVerdict v = Judge(@"Music\Moonward\Glass Harbour Vol. 3", ["01 Lantern Song.flac"], new SlskdSearchData("Moonward", "Glass Harbour Vol. 2", false, false, 1, null));

        Assert.False(v.Matched);
        Assert.Contains("volume series differs", v.Reasons);
        Assert.DoesNotContain("album not named", v.Reasons);
    }

    [Fact]
    public void an_artist_only_search_gives_no_album_reason()
    {
        SlskdFolderVerdict v = Judge(@"Music\Moonward\Glass Harbour", ["01 Lantern Song.flac"], new SlskdSearchData("Moonward", null, false, false, 1, null));

        Assert.True(v.Matched);
        Assert.Equal(["artist named"], v.Reasons);
    }

    [Fact]
    public void the_track_title_share_reads_the_same_in_any_culture()
    {
        System.Globalization.CultureInfo saved = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            SlskdFolderVerdict v = Judge(@"Music\Unsorted", ["01 Lantern Song.flac", "02 Tidewater.flac"],
                new SlskdSearchData("Moonward", "Glass Harbour", false, false, 1, null, TrackCount: 2, Tracks: ["Lantern Song", "Tidewater"]));

            Assert.Contains("track titles 100%", v.Reasons);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void a_volume_search_names_the_volume_route()
    {
        SlskdFolderVerdict v = Judge(@"Music\Moonward\Glass Harbour Vol. 2", ["01 Lantern Song.flac"], new SlskdSearchData("Moonward", "Glass Harbour Vol. 2", false, false, 1, null));

        Assert.True(v.Matched);
        Assert.Contains("volume series matches", v.Reasons);
    }

    [Fact]
    public void track_titles_that_carry_the_match_are_counted()
    {
        SlskdFolderVerdict v = Judge(@"Music\Unsorted", ["01 Lantern Song.flac", "02 Tidewater.flac"],
            new SlskdSearchData("Moonward", "Glass Harbour", false, false, 1, null, TrackCount: 2, Tracks: ["Lantern Song", "Tidewater"]));

        Assert.True(v.Matched);
        Assert.Contains("track titles 100%", v.Reasons);
    }

    [Fact]
    public void a_different_remix_is_rejected_with_its_reason()
    {
        SlskdFolderVerdict v = Judge(@"Music\Moonward\Lantern Song (Harbour Remix)", ["01 Lantern Song (Harbour Remix).flac"],
            new SlskdSearchData("Moonward", "Lantern Song", false, false, 1, null));

        Assert.False(v.Matched);
        Assert.Contains("remix or version qualifier differs", v.Reasons);
    }

    [Fact]
    public void a_single_absent_from_the_folder_is_rejected_with_its_reason()
    {
        SlskdFolderVerdict v = Judge(@"Music\Moonward\Glass Harbour", ["01 Tidewater.flac", "02 Salt Road.flac"],
            new SlskdSearchData("Moonward", "Glass Harbour", false, false, 1, null, TrackCount: 1, Tracks: ["Lantern Song"], AlbumType: "Single"), expected: 1);

        Assert.False(v.Matched);
        Assert.Contains("holds none of the wanted tracks", v.Reasons);
    }

    [Fact]
    public void a_single_taken_from_a_bigger_folder_says_how_many_files()
    {
        SlskdFolderVerdict v = Judge(@"Music\Moonward\Glass Harbour", ["01 Tidewater.flac", "02 Lantern Song.flac", "03 Salt Road.flac"],
            new SlskdSearchData("Moonward", "Lantern Song", false, false, 1, null, TrackCount: 1, Tracks: ["Lantern Song"], AlbumType: "Single"), expected: 1);

        Assert.Contains("took 1 of 3 audio files", v.Reasons);
        Assert.Single(v.FilesToDownload);
    }

    [Fact]
    public void a_partial_source_under_whole_albums_only_is_rejected_with_its_reason()
    {
        SlskdFolderVerdict v = Judge(@"Music\Moonward\Glass Harbour", ["01 Lantern Song.flac"],
            new SlskdSearchData("Moonward", "Glass Harbour", false, false, 1, null, TrackCount: 2, Tracks: ["Lantern Song", "Tidewater"], AlbumType: "EP"),
            new SlskdSettings().WithWholeAlbumsOnly(true), expected: 2);

        Assert.False(v.Matched);
        Assert.Contains("holds 1 of 2 matchable tracks and Whole Albums Only is on", v.Reasons);
    }

    [Fact]
    public void match_release_tracks_names_the_mismatch()
    {
        SlskdFolderVerdict v = Judge(@"Music\Moonward\Glass Harbour", ["01 Lantern Song.flac", "02 Tidewater.flac", "03 Bonus.flac"],
            new SlskdSearchData("Moonward", "Glass Harbour", false, false, 1, null, TrackCount: 2, Tracks: ["Lantern Song", "Tidewater"], AlbumType: "Album"),
            new SlskdSettings { MatchReleaseTracks = true }, expected: 2);

        Assert.False(v.Matched);
        Assert.Contains("Match Release Tracks: 3 audio file(s) for 2 track(s)", v.Reasons);
    }
}
