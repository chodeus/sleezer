using NLog;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek;
using Xunit;

namespace Sleezer.Tests;

// DJ-pool files carry edit markers in the file name: they are other cuts of the song.
public class DjPoolEditTests
{
    [Theory]
    [InlineData("Lantern Song (90s ReDrum) (5A - 95)")]
    [InlineData("Lantern Song (Re-Drum)")]
    [InlineData("Lantern Song {ReDrum}")]
    [InlineData("Lantern Song (Quick Hit)")]
    [InlineData("Lantern Song (Intro) (Clean)")]
    [InlineData("Lantern Song (Intro - Dirty)")]
    [InlineData("Lantern Song (Clean Outro)")]
    [InlineData("Lantern Song {Intro} {Clean}")]
    public void A_dj_pool_edit_is_not_the_plain_track(string candidate) =>
        Assert.True(VariantQualifiers.RemixSignaturesConflict("Lantern Song", candidate));

    // MusicBrainz lists some DJ-intro releases outright: the plain track is not one.
    [Theory]
    [InlineData("Lantern Song", true)]
    [InlineData("Lantern Song (DJ Intro) (Clean)", false)]
    public void A_dj_intro_release_calls_for_its_edit(string candidate, bool conflicts) =>
        Assert.Equal(conflicts, VariantQualifiers.RemixSignaturesConflict("Lantern Song (DJ intro) (clean)", candidate));

    // Left out on purpose: these mark the same recording, or a name, in real peer files.
    [Theory]
    [InlineData("Lantern Song (Intro)")]
    [InlineData("Lantern Song (Clean)")]
    [InlineData("Lantern Song [Dirty]")]
    [InlineData("5A - 125 - Lantern Song")]
    [InlineData("Lantern Song (Moonward Intro Mix)")]
    public void A_lone_marker_is_no_edit(string candidate) =>
        Assert.False(VariantQualifiers.RemixSignaturesConflict("Lantern Song", candidate));

    // A track named "Intro" or "Dirty" carries a lone marker: the pair must start inside a bracket.
    [Theory]
    [InlineData("Intro", "Intro (Clean)")]
    [InlineData("Outro", "Outro [Dirty]")]
    [InlineData("Dirty", "Dirty (Intro)")]
    public void A_title_word_does_not_pair_with_a_bracketed_marker(string wanted, string candidate) =>
        Assert.False(VariantQualifiers.RemixSignaturesConflict(wanted, candidate));

    // Clean and dirty, or intro and outro, are different cuts under the same edit label.
    [Theory]
    [InlineData("Lantern Song (Intro) (Clean)", "Lantern Song (Intro) (Dirty)", true)]
    [InlineData("Lantern Song (Intro) (Clean)", "Lantern Song (Outro) (Clean)", true)]
    [InlineData("Lantern Song (Intro) (Dirty)", "Clean Harbour - Lantern Song (Intro) (Dirty)", false)]
    [InlineData("Lantern Song (Quick Hit) (Clean)", "Dirty Harbour - Lantern Song (Quick Hit)", false)]
    public void Dj_cuts_differ_by_rating_and_end(string wanted, string candidate, bool conflicts) =>
        Assert.Equal(conflicts, VariantQualifiers.RemixSignaturesConflict(wanted, candidate));

    // A remixer's name is no cut; a cut label after the remix word still is.
    [Theory]
    [InlineData("Lantern Song (Dirty Harbour Remix)", "Lantern Song (Dirty Harbour Remix) (Clean)", false)]
    [InlineData("Lantern Song (Clean Harbour Remix)", "Lantern Song (Clean Harbour Remix) (Dirty)", false)]
    [InlineData("Lantern Song (Moonward Remix - Dirty Harbour Dub)", "Lantern Song (Moonward Remix - Dirty Harbour Dub) (Clean)", false)]
    [InlineData("Lantern Song (Dirty Harbour Remix - Intro Clean)", "Lantern Song (Dirty Harbour Remix - Intro Dirty)", true)]
    [InlineData("Lantern Song (Moonward Remix - Clean)", "Lantern Song (Moonward Remix) (Dirty)", true)]
    public void A_remixer_name_is_no_dj_cut(string wanted, string candidate, bool conflicts) =>
        Assert.Equal(conflicts, VariantQualifiers.RemixSignaturesConflict(wanted, candidate));

    // A cut split over the path is still an edit; a lone marker on either level is not.
    [Theory]
    [InlineData("Lantern Song (Intro)", "DJ Pool (Clean)", true)]
    [InlineData("Lantern Song (Intro)", "DJ Pool", false)]
    [InlineData("Lantern Song", "DJ Pool (Clean)", false)]
    [InlineData("Lantern Song (Intro) (5A - 95) (Clean)", "DJ Pool", true)]
    public void A_dj_cut_split_over_the_path_is_an_edit(string leaf, string parent, bool conflicts) =>
        Assert.Equal(conflicts, VariantQualifiers.RemixSignaturesConflict("Lantern Song", [leaf, parent], null));

    // Markers in separate brackets make an edit in one name as in a path, wanted or found.
    [Theory]
    [InlineData("Lantern Song", "Lantern Song (Intro) (5A - 95) (Clean)", true)]
    [InlineData("Lantern Song (DJ intro - DJ outro 1) (clean)", "Lantern Song", true)]
    [InlineData("Lantern Song (DJ intro - DJ outro 1) (clean)", "Lantern Song (DJ intro - DJ outro 1) (clean)", false)]
    public void Dj_cut_markers_in_separate_brackets_make_an_edit(string wanted, string candidate, bool conflicts) =>
        Assert.Equal(conflicts, VariantQualifiers.RemixSignaturesConflict(wanted, candidate));

    // The innermost folder naming a cut decides: a dirty file in a "(Clean)" pool folder is dirty.
    [Theory]
    [InlineData("Lantern Song (Intro) (Dirty)", "DJ Pool (Clean)", true)]
    [InlineData("Lantern Song (Intro) (Clean)", "DJ Pool (Dirty)", false)]
    [InlineData("Lantern Song (Intro) (Clean) (Dirty)", "DJ Pool", true)]
    public void The_innermost_component_naming_a_cut_decides(string leaf, string parent, bool conflicts) =>
        Assert.Equal(conflicts, VariantQualifiers.RemixSignaturesConflict("Lantern Song (Intro) (Clean)", [leaf, parent], null));

    [Theory]
    [InlineData("Lantern Song (90s ReDrum)")]
    [InlineData("Lantern Song (ReDrum)")]
    [InlineData("Lantern Song {ReDrum}")]
    public void A_redrum_is_a_remix_track(string title) =>
        Assert.True(VariantQualifiers.IsVariantTrack(title));

    // A bracketed ReDrum is the qualifier, not the dash tail around it.
    [Fact]
    public void A_bracketed_redrum_in_a_file_name_is_a_generic_label() =>
        Assert.Equal(string.Empty, VariantQualifiers.ExtractRemixSignature("Moonward - Lantern Song (ReDrum)"));

    // A song can be called "Redrum": as a bare title it is no remix word, and its plain single still refuses a remix.
    [Fact]
    public void A_bare_redrum_title_is_a_song()
    {
        Assert.False(VariantQualifiers.IsVariantTrack("Redrum"));
        Assert.True(VariantQualifiers.RemixSignaturesConflict("Redrum", "Redrum (Moonward Remix)"));
        Assert.False(VariantQualifiers.IsVariantTrack("Redrum (Murder)"));
        Assert.Null(VariantQualifiers.ExtractRemixSignature("Redrum (Murder)"));
    }

    // A plainly named folder of DJ-pool files: the wanted title must not claim the edit.
    [Fact]
    public void A_folder_of_dj_pool_edits_does_not_match_the_plain_single()
    {
        List<SlskdFileData> files = [new SlskdFileData(@"Music\Moonward\Lantern Song\01 Lantern Song (Intro) (Clean).mp3", null, 16, 9_000_000, 210, ".mp3", 44100, 0, false)];
        SlskdSearchData search = new("Moonward", "Lantern Song", false, false, 1, null, TrackCount: 1, Tracks: ["Lantern Song"], AlbumType: "Single");

        SlskdFolderVerdict verdict = new SlskdItemsParser(LogManager.GetLogger("tests")).JudgeFolder(files.GroupBy(f => SlskdTextProcessor.GetMergedDirectoryKey(f.Filename)).Single(), search, null, 1);

        Assert.False(verdict.Matched);
    }
}
