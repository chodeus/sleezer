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
    [InlineData("Lantern Song (Quick Hit)")]
    [InlineData("Lantern Song (Intro) (Clean)")]
    [InlineData("Lantern Song (Intro - Dirty)")]
    [InlineData("Lantern Song (Clean Outro)")]
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

    // Clean and dirty, or intro and outro, are different cuts under the same edit label.
    [Theory]
    [InlineData("Lantern Song (Intro) (Clean)", "Lantern Song (Intro) (Dirty)", true)]
    [InlineData("Lantern Song (Intro) (Clean)", "Lantern Song (Outro) (Clean)", true)]
    [InlineData("Lantern Song (Intro) (Dirty)", "Clean Bandit - Lantern Song (Intro) (Dirty)", false)]
    [InlineData("Lantern Song (Quick Hit) (Clean)", "Dirty Harbour - Lantern Song (Quick Hit)", false)]
    public void Dj_cuts_differ_by_rating_and_end(string wanted, string candidate, bool conflicts) =>
        Assert.Equal(conflicts, VariantQualifiers.RemixSignaturesConflict(wanted, candidate));

    [Fact]
    public void A_redrum_is_a_remix_track() =>
        Assert.True(VariantQualifiers.IsVariantTrack("Lantern Song (90s ReDrum)"));

    // A song can be called "Redrum": as a bare title it is no remix word, and its plain single still refuses a remix.
    [Fact]
    public void A_bare_redrum_title_is_a_song()
    {
        Assert.False(VariantQualifiers.IsVariantTrack("Redrum"));
        Assert.True(VariantQualifiers.RemixSignaturesConflict("Redrum", "Redrum (Moonward Remix)"));
        Assert.False(VariantQualifiers.RemixSignaturesConflict("Redrum (Murder)", "Redrum (Murder)"));
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
