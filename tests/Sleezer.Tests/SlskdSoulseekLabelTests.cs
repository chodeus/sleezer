using NLog;
using NzbDrone.Plugin.Sleezer.Core.Model;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek;
using Xunit;

namespace Sleezer.Tests;

// sleezer guessed [WEB] for every slskd folder that named no source, so a Soulseek result
// took the same WEB custom-format score as a store download and could outrank it.
public class SlskdSoulseekLabelTests
{
    private static readonly SlskdItemsParser Parser = new(LogManager.GetLogger("tests"));

    private static string TitleFor(string folder, bool label)
    {
        List<SlskdFileData> files = [new($@"{folder}\01 Lantern Song.flac", null, 16, 30_000_000, 212, ".flac", 44100, 0, false)];
        AlbumData album = Parser.CreateAlbumData(
            files.GroupBy(f => SlskdTextProcessor.GetMergedDirectoryKey(f.Filename)).Single(),
            new SlskdSearchData("Moonward", "Glass Harbour", false, false, 1, null, TrackCount: 1, Tracks: ["Lantern Song"], AlbumType: "Album"),
            new SlskdFolderData(folder, "Moonward", "Glass Harbour", "", "peer", true, 1_000_000, 0, [], 0, 0, 0, [], 0),
            new SlskdSettings { LabelSoulseekResults = label },
            expectedTrackCount: 1);
        return album.ToShareInfo().Title;
    }

    [Fact]
    public void off_a_folder_naming_no_source_is_guessed_web()
    {
        Assert.EndsWith(" [FLAC 16bit 44.1kHz] [WEB]", TitleFor(@"Music\Moonward\Glass Harbour", label: false));
    }

    [Fact]
    public void on_a_folder_naming_no_source_is_labelled_soulseek()
    {
        string title = TitleFor(@"Music\Moonward\Glass Harbour", label: true);

        Assert.EndsWith(" [FLAC 16bit 44.1kHz] [Soulseek]", title);
        Assert.DoesNotContain("WEB", title);
    }

    [Fact]
    public void on_a_web_claim_in_the_folder_is_dropped()
    {
        string title = TitleFor(@"Music\Moonward\Glass Harbour [WEB]", label: true);

        Assert.EndsWith(" [FLAC 16bit 44.1kHz] [Soulseek]", title);
        Assert.DoesNotContain("WEB", title);
    }

    [Theory]
    [InlineData(@"Music\Moonward\Glass Harbour (Vinyl)", " [Vinyl] [Soulseek]")]
    [InlineData(@"Music\Moonward\Glass Harbour [SACD]", " [SACD] [Soulseek]")]
    [InlineData(@"Music\Moonward\Glass Harbour [CD]", " [CD] [Soulseek]")]
    public void on_a_rip_medium_named_in_the_folder_is_kept_before_the_label(string folder, string ending)
    {
        Assert.EndsWith(ending, TitleFor(folder, label: true));
    }

    [Fact]
    public void on_an_edition_is_kept_alongside_the_medium()
    {
        Assert.EndsWith(" [DELUXE EDITION] [Vinyl] [Soulseek]", TitleFor(@"Music\Moonward\Glass Harbour (Deluxe Edition) [Vinyl]", label: true));
    }

    [Fact]
    public void the_setting_is_off_by_default_and_survives_whole_albums_only()
    {
        Assert.False(new SlskdSettings().LabelSoulseekResults);
        Assert.True(new SlskdSettings { LabelSoulseekResults = true }.WithWholeAlbumsOnly(true).LabelSoulseekResults);
    }
}
