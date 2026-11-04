using NLog;
using NzbDrone.Plugin.Sleezer.Core.Model;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek;
using Xunit;

namespace Sleezer.Tests;

public class SlskdReleaseTracksTests
{
    private const string Folder = @"Music\Moonward\Glass Harbour";
    private static readonly SlskdItemsParser Parser = new(LogManager.GetLogger("tests"));

    private static SlskdFileData File(string name, int? seconds) =>
        new($@"{Folder}\{name}.flac", null, 16, 30_000_000, seconds, ".flac", 44100, 0, false);

    [Fact]
    public void files_that_match_count_titles_and_lengths_are_the_release()
    {
        Assert.Null(SlskdReleaseTracks.Mismatch([File("01 Lantern Song", 212), File("02 Tidewater", 301)], 2, 2, 2, [210_000, 305_000]));
    }

    [Fact]
    public void a_folder_holding_more_files_than_the_release_has_tracks_is_not_the_release()
    {
        string? reason = SlskdReleaseTracks.Mismatch([File("01 Lantern Song", 212), File("02 Tidewater", 301), File("03 Bonus", 180)], 2, 2, 2, [210_000, 305_000]);

        Assert.Equal("3 audio file(s) for 2 track(s)", reason);
    }

    private static SlskdFileData Sized(string path, long size, int seconds) =>
        new(path, null, 16, size, seconds, Path.GetExtension(path), 44100, 0, false);

    [Fact]
    public void the_same_lossless_file_twice_is_not_the_release()
    {
        string? reason = SlskdReleaseTracks.Mismatch(
            [Sized($@"{Folder}\01 Lantern Song (Harbour Remix).flac", 25_695_594, 181), Sized($@"{Folder}\02 Lantern Song.flac", 25_695_594, 181)], 2, 2, 2, [180_000, 187_000]);

        Assert.Equal("02 Lantern Song.flac is in the folder twice", reason);
    }

    [Fact]
    public void lossless_copies_that_differ_only_by_tags_are_not_the_release()
    {
        string? reason = SlskdReleaseTracks.Mismatch(
            [Sized($@"{Folder}\01 Lantern Song.flac", 25_593_342, 211), Sized($@"{Folder}\02 Lantern Song (Night Version).flac", 25_593_376, 211)], 2, 2, 2, [210_000, 211_000]);

        Assert.Equal("02 Lantern Song (Night Version).flac is in the folder twice", reason);
    }

    [Fact]
    public void one_song_in_two_formats_is_not_the_release()
    {
        string? reason = SlskdReleaseTracks.Mismatch(
            [Sized($@"{Folder}\Lantern Song.flac", 15_856_283, 121), Sized($@"{Folder}\Lantern Song.mp3", 5_738_349, 121)], 2, 2, 2, [121_000, 123_000]);

        Assert.Equal("Lantern Song.mp3 is in the folder twice", reason);
    }

    [Fact]
    public void lossy_tracks_of_one_length_and_size_can_still_be_the_release()
    {
        Assert.Null(SlskdReleaseTracks.Mismatch(
            [Sized($@"{Folder}\01 Lantern Song.mp3", 9_600_000, 240), Sized($@"{Folder}\02 Tidewater.mp3", 9_600_000, 240)], 2, 2, 2, [240_000, 240_000]));
    }

    // An uncompressed track's size follows from its length alone.
    [Theory]
    [InlineData(".wav")]
    [InlineData(".aiff")]
    public void uncompressed_tracks_of_one_length_can_still_be_the_release(string extension)
    {
        Assert.Null(SlskdReleaseTracks.Mismatch(
            [Sized($@"{Folder}\01 Lantern Song{extension}", 37_044_044, 210), Sized($@"{Folder}\02 Tidewater{extension}", 37_046_396, 210)], 2, 2, 2, [210_000, 210_000]));
    }

    [Fact]
    public void one_song_in_two_formats_is_found_whatever_unicode_form_its_names_use()
    {
        string? reason = SlskdReleaseTracks.Mismatch(
            [Sized(Folder + "\\Caf\u00E9 Tide.flac", 15_856_283, 121), Sized(Folder + "\\Cafe\u0301 Tide.mp3", 5_738_349, 121)], 2, 2, 2, [121_000, 123_000]);

        Assert.NotNull(reason);
    }

    [Theory]
    [InlineData("02 Silence", "03 Silence")]
    [InlineData("Moonward - Glass Harbour - 02 - Silence", "Moonward - Glass Harbour - 03 - Silence")]
    public void a_release_that_repeats_a_track_can_still_be_the_release(string first, string second)
    {
        Assert.Null(SlskdReleaseTracks.Mismatch(
            [Sized($@"{Folder}\01 Lantern Song.flac", 25_000_000, 211), Sized($@"{Folder}\{first}.flac", 401_234, 4), Sized($@"{Folder}\{second}.flac", 401_234, 4)], 3, 3, 3, [211_000, 4_000, 4_000]));
    }

    // Through the parser: each title finds a file, so only the copy rule refuses it; far sizes are two recordings.
    [Theory]
    [InlineData(25_593_376, false)]
    [InlineData(27_000_000, true)]
    public void on_one_recording_under_two_titles_does_not_match(long secondSize, bool matched)
    {
        Assert.Equal(matched, MatchedRelease(
            ["Lantern Song", "Tidewater"], [211_000, 211_000],
            [Sized($@"{Folder}\01 Lantern Song.flac", 25_593_342, 211), Sized($@"{Folder}\02 Tidewater.flac", secondSize, 211)]));
    }

    // The title check, not the copy rule, refuses a song twice under its own title.
    [Fact]
    public void on_a_song_twice_under_its_own_title_does_not_match()
    {
        Assert.False(MatchedRelease(
            ["Lantern Song", "Tidewater"], [211_000, 211_000],
            [Sized($@"{Folder}\01 Lantern Song.flac", 25_593_342, 211), Sized($@"{Folder}\02 Lantern Song.flac", 25_593_342, 211)]));
    }

    [Fact]
    public void on_a_release_that_repeats_a_track_matches()
    {
        Assert.True(MatchedRelease(
            ["Lantern Song", "Silence", "Silence"], [211_000, 4_000, 4_000],
            [Sized($@"{Folder}\01 Lantern Song.flac", 25_000_000, 211), Sized($@"{Folder}\02 Silence.flac", 401_234, 4), Sized($@"{Folder}\03 Silence.flac", 401_234, 4)]));
    }

    [Fact]
    public void same_named_files_on_two_discs_can_still_be_the_release()
    {
        Assert.Null(SlskdReleaseTracks.Mismatch(
            [Sized($@"{Folder}\CD1\01 Intro.flac", 9_000_000, 60), Sized($@"{Folder}\CD2\01 Intro.flac", 9_400_000, 60)], 2, 2, 2, [60_000, 60_000]));
    }

    [Fact]
    public void lossless_tracks_of_one_length_but_other_sizes_can_still_be_the_release()
    {
        Assert.Null(SlskdReleaseTracks.Mismatch(
            [Sized($@"{Folder}\01 Lantern Song.flac", 25_000_000, 211), Sized($@"{Folder}\02 Tidewater.flac", 26_000_000, 211)], 2, 2, 2, [211_000, 211_000]));
    }

    [Fact]
    public void a_folder_missing_a_track_is_not_the_release()
    {
        Assert.Equal("1 audio file(s) for 2 track(s)", SlskdReleaseTracks.Mismatch([File("01 Lantern Song", 212)], 2, 1, 2, [210_000, 305_000]));
    }

    [Fact]
    public void a_track_title_absent_from_the_file_names_fails()
    {
        Assert.Equal("1 of 2 track titles found in the file names", SlskdReleaseTracks.Mismatch([File("01 Lantern Song", 212), File("02 Other", 301)], 2, 1, 2, [210_000, 305_000]));
    }

    [Theory]
    [InlineData(218, true)]
    [InlineData(202, true)]
    [InlineData(219, false)]
    [InlineData(201, false)]
    public void a_track_length_must_be_within_eight_seconds_of_a_file(int fileSeconds, bool matches)
    {
        string? reason = SlskdReleaseTracks.Mismatch([File("01 Lantern Song", fileSeconds)], 1, 1, 1, [210_000]);

        Assert.Equal(matches ? null : "0 of 1 track lengths within 8s of a file", reason);
    }

    [Fact]
    public void each_file_pairs_with_only_one_track()
    {
        string? reason = SlskdReleaseTracks.Mismatch([File("01 Lantern Song", 235), File("02 Tidewater", 400)], 2, 2, 2, [235_000, 240_000]);

        Assert.Equal("1 of 2 track lengths within 8s of a file", reason);
    }

    [Fact]
    public void a_file_without_a_reported_length_fails_when_the_release_lists_lengths()
    {
        Assert.Equal("1 of 2 track lengths within 8s of a file", SlskdReleaseTracks.Mismatch([File("01 Lantern Song", 212), File("02 Tidewater", null)], 2, 2, 2, [210_000, 305_000]));
    }

    [Fact]
    public void a_release_with_no_known_lengths_is_judged_on_count_and_titles()
    {
        Assert.Null(SlskdReleaseTracks.Mismatch([File("01 Lantern Song", 212), File("02 Tidewater", null)], 2, 2, 2, [0, 0]));
        Assert.Null(SlskdReleaseTracks.Mismatch([File("01 Lantern Song", 212), File("02 Tidewater", null)], 2, 2, 2, null));
    }

    [Fact]
    public void a_release_whose_track_count_is_unknown_cannot_be_matched()
    {
        Assert.Equal("the release's track count is unknown", SlskdReleaseTracks.Mismatch([File("01 Lantern Song", 212)], 0, 1, 1, [210_000]));
    }

    [Fact]
    public void off_a_folder_of_the_wrong_lengths_still_matches_by_name()
    {
        Assert.True(Matched(matchReleaseTracks: false, [File("01 Lantern Song", 360), File("02 Tidewater", 301)]));
    }

    [Fact]
    public void on_a_folder_of_the_wrong_lengths_does_not_match()
    {
        Assert.False(Matched(matchReleaseTracks: true, [File("01 Lantern Song", 360), File("02 Tidewater", 301)]));
    }

    [Fact]
    public void on_a_folder_with_an_extra_file_does_not_match()
    {
        Assert.False(Matched(matchReleaseTracks: true, [File("01 Lantern Song", 131), File("02 Tidewater", 301), File("03 Bonus", 180)]));
    }

    [Fact]
    public void on_the_release_itself_matches()
    {
        Assert.True(Matched(matchReleaseTracks: true, [File("01 Lantern Song", 131), File("02 Tidewater", 301)]));
    }

    [Fact]
    public void on_an_artist_search_is_not_judged_against_a_release()
    {
        AlbumData album = Parser.CreateAlbumData(
            Group([File("01 Lantern Song", 360)]),
            new SlskdSearchData("Moonward", null, false, false, 1, null, TrackCount: 2, Tracks: ["Lantern Song", "Tidewater"], TrackDurations: [130_000, 300_000]),
            FolderData(),
            new SlskdSettings { MatchReleaseTracks = true },
            expectedTrackCount: 2);

        Assert.True(album.MatchedSearchCriteria);
    }

    [Fact]
    public void on_a_single_plucked_from_a_larger_folder_matches()
    {
        List<SlskdFileData> albumFolder = [File("01 Tidewater", 301), File("02 Lantern Song", 131), File("03 Salt Road", 250), File("04 Undertow", 199)];

        AlbumData single = Parser.CreateAlbumData(
            Group(albumFolder),
            new SlskdSearchData("Moonward", "Lantern Song", false, false, 1, null, TrackCount: 1, Tracks: ["Lantern Song"], AlbumType: "Single", TrackDurations: [130_000]),
            FolderData(),
            new SlskdSettings { MatchReleaseTracks = true },
            expectedTrackCount: 1);

        Assert.True(single.MatchedSearchCriteria);
    }

    [Fact]
    public void the_setting_is_off_by_default_and_survives_whole_albums_only()
    {
        Assert.False(new SlskdSettings().MatchReleaseTracks);
        Assert.True(new SlskdSettings { MatchReleaseTracks = true }.WithWholeAlbumsOnly(true).MatchReleaseTracks);
    }

    private static bool Matched(bool matchReleaseTracks, List<SlskdFileData> files) =>
        Parser.CreateAlbumData(
            Group(files),
            new SlskdSearchData("Moonward", "Glass Harbour", false, false, 1, null, TrackCount: 2, Tracks: ["Lantern Song", "Tidewater"], AlbumType: "Album", TrackDurations: [130_000, 300_000]),
            FolderData(),
            new SlskdSettings { MatchReleaseTracks = matchReleaseTracks },
            expectedTrackCount: 2).MatchedSearchCriteria;

    private static bool MatchedRelease(string[] tracks, int[] durationsMs, List<SlskdFileData> files) =>
        Parser.CreateAlbumData(
            Group(files),
            new SlskdSearchData("Moonward", "Glass Harbour", false, false, 1, null, TrackCount: tracks.Length, Tracks: [.. tracks], AlbumType: "Album", TrackDurations: [.. durationsMs]),
            FolderData(),
            new SlskdSettings { MatchReleaseTracks = true },
            expectedTrackCount: tracks.Length).MatchedSearchCriteria;

    private static IGrouping<string, SlskdFileData> Group(List<SlskdFileData> files) =>
        files.GroupBy(f => SlskdTextProcessor.GetMergedDirectoryKey(f.Filename)).Single();

    private static SlskdFolderData FolderData() =>
        new(Folder, "Moonward", "Glass Harbour", "", "peer", true, 1_000_000, 0, [], 0, 0, 0, [], 0);
}
