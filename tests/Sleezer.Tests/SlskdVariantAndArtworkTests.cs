using NLog;
using NzbDrone.Plugin.Sleezer.Core.Model;
using NzbDrone.Plugin.Sleezer.Core.PostProcessing;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek;
using Xunit;

namespace Sleezer.Tests;

// Two 2026-08-07 follow-ups from the GLXY audit:
//  1. A malformed embedded cover-art block aborts ffmpeg's INPUT OPEN under
//     -err_detect explode, before -map 0:a limits the scan to audio. Verified
//     against ffmpeg 8.1.2: the decoded audio of such a file is byte-identical
//     to a clean one, yet the scanner deleted + blocklisted it (GLXY "Love Lost"
//     lost this way from two different peers).
//  2. A wanted track title is CONTAINED in its own variant ("Proposition" in
//     "Proposition (Radio Edit)"), so a radio-edit-only source looked complete
//     and was grabbed; Lidarr then rejected the import on track length.
public class AttachedPictureFalsePositiveTests
{
    private const string PictureFailure =
        "[in#0 @ 0x1] Could not read mimetype from an attached picture.\n" +
        "[in#0 @ 0x1] Error parsing attached picture.\n" +
        "[in#0 @ 0x2] Error opening input: Invalid data found when processing input\n" +
        "Error opening input file /downloads/GLXY - Love Lost/01 - Love Lost.flac.\n";

    [Fact]
    public void The_live_love_lost_stderr_is_recognised_as_an_artwork_failure()
    {
        Assert.True(FfmpegErrorFormatter.IsAttachedPictureFailure(PictureFailure));
    }

    [Fact]
    public void Artwork_lines_are_stripped_as_benign_metadata_noise()
    {
        Assert.True(string.IsNullOrWhiteSpace(
            FfmpegErrorFormatter.StripBenignMetadataNoise(
                "[in#0 @ 0x1] Could not read mimetype from an attached picture.\n")));
    }

    // Fail-closed: the re-verify pass keeps real decoder errors (observed exit
    // 183 with these exact lines on a bad-picture + damaged-audio file).
    [Fact]
    public void Real_decoder_errors_survive_the_artwork_strip()
    {
        string stderr =
            "[in#0 @ 0x1] Could not read mimetype from an attached picture.\n" +
            "[flac @ 0x2] invalid sync code\n" +
            "[flac @ 0x2] decode_frame() failed\n";

        string significant = FfmpegErrorFormatter.StripBenignMetadataNoise(stderr);

        Assert.False(string.IsNullOrWhiteSpace(significant));
        Assert.Contains("invalid sync code", significant);
        Assert.DoesNotContain("attached picture", significant);
    }

    // A truncated file carries no artwork marker, so it never reaches the
    // re-verify path and keeps failing on the first pass.
    [Theory]
    [InlineData("[in#0 @ 0x1] Error opening input: End of file\nError opening input file /tmp/trunc.flac.\n")]
    [InlineData("[flac @ 0x2] Header missing\n")]
    [InlineData("")]
    public void Non_artwork_failures_are_not_treated_as_artwork(string stderr)
    {
        Assert.False(FfmpegErrorFormatter.IsAttachedPictureFailure(stderr));
    }
}

public class VariantQualifierDetectionTests
{
    [Theory]
    [InlineData("GLXY - Proposition (Radio Edit) [feat. James Robb]", true)]
    [InlineData("Best of Both Worlds (Live)", true)]
    [InlineData("Dreams (Extended Version)", true)]
    [InlineData("Never Say Never (Colyn Remix)", true)]
    [InlineData("Proposition", false)]
    [InlineData("GLXY - Mind Less", false)]
    [InlineData("OK Computer (Deluxe Edition)", false)]   // edition, not a variant
    [InlineData("Live Forever", false)]                    // "live" as a title word
    [InlineData("", false)]
    public void Qualifier_detection_matches_the_variant_profile(string title, bool expected)
    {
        Assert.Equal(expected, SlskdTextProcessor.HasVariantQualifier(title));
    }
}

public class RadioEditTrackMatchingTests
{
    private static readonly SlskdItemsParser Parser = new(LogManager.GetLogger("tests"));

    private static IGrouping<string, SlskdFileData> Group(params string[] filenames) =>
        filenames
            .Select(f => new SlskdFileData(f, null, 16, 30_000_000, 300, ".flac", 44100, 0, false))
            .GroupBy(f => SlskdTextProcessor.GetMergedDirectoryKey(f.Filename))
            .Single();

    private static SlskdFolderData Folder(string path) =>
        new(path, "", "", "", "peer", true, 1_000_000, 0, [], 0, 0, 0, [], 0);

    private static SlskdSearchData Search(List<string> tracks, string album = "Proposition Mind Less",
        List<string>? variantTypes = null, string albumType = "Single") =>
        new("GLXY", album, Interactive: false, ExpandDirectory: false, MinimumFiles: 1, MaximumFiles: 4,
            TrackCount: tracks.Count, Tracks: tracks, TargetVariantTypes: variantTypes, AlbumType: albumType);

    // The live 2026-08-06 grab: both files are radio edits of tracks the target
    // lists plainly, so the source holds NONE of the wanted recordings.
    [Fact]
    public void A_radio_edit_only_source_no_longer_satisfies_plain_wanted_tracks()
    {
        AlbumData album = Parser.CreateAlbumData(
            "s1",
            Group(
                @"Music\GLXY - Proposition # Mind Less (2017)\01. GLXY - Proposition (Radio Edit) [feat. James Robb].flac",
                @"Music\GLXY - Proposition # Mind Less (2017)\02. GLXY - Mind Less (Radio Edit) [feat. Blake].flac"),
            Search(["Proposition", "Mind Less"]),
            Folder(@"Music\GLXY - Proposition # Mind Less (2017)"),
            new SlskdSettings { RequireCoherentSingleSource = true },
            expectedTrackCount: 2);

        Assert.False(album.MatchedSearchCriteria);
    }

    [Fact]
    public void The_plain_versions_of_the_same_release_still_match()
    {
        AlbumData album = Parser.CreateAlbumData(
            "s1",
            Group(
                @"Music\GLXY - Proposition # Mind Less (2017)\01. GLXY - Proposition.flac",
                @"Music\GLXY - Proposition # Mind Less (2017)\02. GLXY - Mind Less.flac"),
            Search(["Proposition", "Mind Less"]),
            Folder(@"Music\GLXY - Proposition # Mind Less (2017)"),
            new SlskdSettings { RequireCoherentSingleSource = true },
            expectedTrackCount: 2);

        Assert.True(album.MatchedSearchCriteria);
    }

    // Regression guard: when the TARGET's own track titles carry the qualifier
    // (radio-edit single, box set listing live cuts), the files must still match.
    [Fact]
    public void A_target_that_wants_the_radio_edits_still_matches_them()
    {
        AlbumData album = Parser.CreateAlbumData(
            "s1",
            Group(
                @"Music\GLXY - Proposition # Mind Less (2017)\01. GLXY - Proposition (Radio Edit) [feat. James Robb].flac",
                @"Music\GLXY - Proposition # Mind Less (2017)\02. GLXY - Mind Less (Radio Edit) [feat. Blake].flac"),
            Search(["Proposition (Radio Edit)", "Mind Less (Radio Edit)"]),
            Folder(@"Music\GLXY - Proposition # Mind Less (2017)"),
            new SlskdSettings { RequireCoherentSingleSource = true },
            expectedTrackCount: 2);

        Assert.True(album.MatchedSearchCriteria);
    }

    // Regression guard for the box-set class seen in the failed-import data
    // (Van Halen live cuts, AC/DC Backtracks): MusicBrainz marks the RELEASE
    // Live while the track titles stay plain — the files must still match.
    // NB: album title is deliberately plain. An album whose TITLE carries the
    // qualifier trips a separate, pre-existing parent-folder conflict — see
    // Parent_folder_conflict_is_a_known_false_negative below.
    [Fact]
    public void A_live_album_with_plain_track_titles_still_matches_live_files()
    {
        AlbumData album = Parser.CreateAlbumData(
            "s1",
            Group(
                @"Music\Van Halen - Tokyo Dome\01. Unchained (live at the Tokyo Dome June 21, 2013).flac",
                @"Music\Van Halen - Tokyo Dome\02. Somebody Get Me a Doctor (live at the Tokyo Dome June 21, 2013).flac"),
            Search(["Unchained", "Somebody Get Me a Doctor"], album: "Tokyo Dome",
                variantTypes: ["Live"], albumType: "Album"),
            Folder(@"Music\Van Halen - Tokyo Dome"),
            new SlskdSettings { RequireCoherentSingleSource = true },
            expectedTrackCount: 2);

        Assert.True(album.MatchedSearchCriteria);
    }

    // Per-file check in isolation: the live files are accepted for plain wanted
    // titles because the RELEASE is marked Live (metaLive forgives them).
    [Fact]
    public void Live_files_do_not_conflict_with_plain_titles_when_the_release_is_live()
    {
        Assert.False(SlskdTextProcessor.RemixSignaturesConflict(
            "Unchained", "Unchained (live at the Tokyo Dome June 21, 2013)", ["Live"]));
        Assert.True(SlskdTextProcessor.RemixSignaturesConflict(
            "Unchained", "Unchained (live at the Tokyo Dome June 21, 2013)", null));
    }

    // DOCUMENTS A PRE-EXISTING DEFECT (not introduced here, not fixed here):
    // the parser treats the leaf AND the parent as independent conflict sources,
    // so an album whose own title carries a variant word is rejected whenever the
    // parent folder is generic. Flip these asserts when that is fixed.
    [Fact]
    public void Parent_folder_conflict_is_a_known_false_negative()
    {
        // leaf reconciles fine...
        Assert.False(SlskdTextProcessor.RemixSignaturesConflict(
            "Tokyo Dome Live", "Van Halen - Tokyo Dome Live", ["Live"]));
        // ...but the generic parent alone reports a conflict, and the parser ORs them.
        Assert.True(SlskdTextProcessor.RemixSignaturesConflict("Tokyo Dome Live", "Music", ["Live"]));
        Assert.True(SlskdTextProcessor.RemixSignaturesConflict("Live at Wembley", "Music", null));
    }

    // A mixed source (one plain track, one radio edit) is partial, not complete.
    [Fact]
    public void A_partly_radio_edit_source_is_only_partially_covered()
    {
        AlbumData album = Parser.CreateAlbumData(
            "s1",
            Group(
                @"Music\GLXY - Proposition # Mind Less (2017)\01. GLXY - Proposition.flac",
                @"Music\GLXY - Proposition # Mind Less (2017)\02. GLXY - Mind Less (Radio Edit).flac"),
            Search(["Proposition", "Mind Less"]),
            Folder(@"Music\GLXY - Proposition # Mind Less (2017)"),
            new SlskdSettings { RequireCoherentSingleSource = true },
            expectedTrackCount: 2);

        Assert.False(album.MatchedSearchCriteria);
    }
}
