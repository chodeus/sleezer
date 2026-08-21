using NLog;
using NzbDrone.Plugin.Sleezer.Core.Model;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek;
using Xunit;

namespace Sleezer.Tests;

// Whitelisted cue/log extras ride along in the download set but must not be
// treated as tracks: codec and bitrate come from the audio subset, while Size
// stays the full download so the queue reports what actually transfers.
public class SlskdExtrasFlowTests
{
    private static readonly SlskdItemsParser Parser = new(LogManager.GetCurrentClassLogger());

    private static SlskdFileData F(string filename, string ext, long size, int? length) => new(
        Filename: filename, BitRate: null, BitDepth: null, Size: size, Length: length,
        Extension: ext, SampleRate: null, Code: 1, IsLocked: false);

    private static AlbumData Build(string dir, SlskdFileData[] files, string artist, string album,
        string[] tracks, int expectedTrackCount, string albumType)
    {
        IGrouping<string, SlskdFileData> group = files.GroupBy(_ => dir).Single();
        SlskdFolderData folder = Parser.ParseFolderName(dir) with
        {
            Username = "user",
            HasFreeUploadSlot = true,
            FileCount = files.Length
        };
        SlskdSearchData search = new(artist, album, false, false, 1, null,
            TrackCount: expectedTrackCount, Tracks: tracks.ToList(), AlbumType: albumType);
        return Parser.CreateAlbumData(group, search, folder, null, expectedTrackCount);
    }

    [Fact]
    public void Album_extras_ride_along_without_skewing_the_quality_analysis()
    {
        const string dir = @"@@u\Artist\Album (2001)";
        AlbumData a = Build(dir,
            [
                F(dir + @"\01 - One.flac", "flac", 30_000_000, 300),
                F(dir + @"\02 - Two.flac", "flac", 30_000_000, 300),
                F(dir + @"\03 - Three.flac", "flac", 30_000_000, 300),
                F(dir + @"\rip.cue", "cue", 2_000_000, null),
                F(dir + @"\rip.log", "log", 1_000_000, null),
            ],
            artist: "Artist", album: "Album", tracks: ["One", "Two", "Three"],
            expectedTrackCount: 3, albumType: "Album");

        Assert.Contains("rip.cue", a.CustomString);
        Assert.Contains("rip.log", a.CustomString);
        Assert.Equal(AudioFormat.FLAC, a.Codec);
        // Size is the whole transfer; the derived bitrate is audio-only (the
        // extras' 3 MB would inflate it to 826 kbps).
        Assert.Equal(93_000_000, a.Size);
        Assert.Equal(800, a.Bitrate);
    }

    [Fact]
    public void A_single_target_pluck_drops_the_album_level_extras()
    {
        const string dir = @"@@u\Artist\5150";
        AlbumData a = Build(dir,
            [
                F(dir + @"\01 - Panama.flac", "flac", 1000, 200),
                F(dir + @"\02 - Jump.flac", "flac", 1000, 200),
                F(dir + @"\04 - Dreams.flac", "flac", 1000, 200),
                F(dir + @"\rip.cue", "cue", 500, null),
                F(dir + @"\rip.log", "log", 500, null),
            ],
            artist: "Artist", album: "Dreams", tracks: ["Dreams"],
            expectedTrackCount: 1, albumType: "Single");

        Assert.True(a.MatchedSearchCriteria);
        Assert.Contains("Dreams", a.CustomString);
        Assert.DoesNotContain("rip.cue", a.CustomString);
        Assert.DoesNotContain("rip.log", a.CustomString);
        Assert.Equal(1000, a.Size);
    }
}
