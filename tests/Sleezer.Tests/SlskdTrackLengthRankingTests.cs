using System.Text.Json;
using NLog;
using NzbDrone.Plugin.Sleezer.Core.Model;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek;
using Xunit;

namespace Sleezer.Tests;

public class SlskdTrackLengthRankingTests
{
    private static readonly SlskdItemsParser Parser = new(LogManager.GetLogger("tests"));

    private static List<SlskdFileData> Files(params int?[] seconds) =>
        [.. seconds.Select((s, i) => new SlskdFileData($@"Music\Muse\The Resistance\{i + 1:00} Track.flac", null, 16, 30_000_000, s, ".flac", 44100, 0, false))];

    [Fact]
    public void every_file_within_the_tolerance_is_a_full_match()
    {
        Assert.Equal(1.0, SlskdItemsParser.TrackLengthMatch(Files(212, 301, 245), [210_000, 305_000, 247_400]));
    }

    [Fact]
    public void a_different_edit_counts_against_the_match()
    {
        Assert.Equal(0.5, SlskdItemsParser.TrackLengthMatch(Files(212, 340, 245, 90), [210_000, 305_000, 247_400, 180_000]));
    }

    [Fact]
    public void a_folder_holding_part_of_the_release_is_judged_on_what_it_holds()
    {
        Assert.Equal(1.0, SlskdItemsParser.TrackLengthMatch(Files(212, 245), [210_000, 305_000, 247_400, 180_000]));
    }

    [Theory]
    [InlineData(new int[0])]
    [InlineData(new[] { 0, 0, 247_400 })]
    public void missing_release_lengths_give_no_verdict(int[] releaseMs)
    {
        Assert.Null(SlskdItemsParser.TrackLengthMatch(Files(212, 301, 245), releaseMs));
    }

    [Fact]
    public void files_without_lengths_give_no_verdict()
    {
        Assert.Null(SlskdItemsParser.TrackLengthMatch(Files(212, null, null), [210_000, 305_000, 247_400]));
    }

    [Fact]
    public void the_folder_whose_lengths_match_ranks_above_one_that_does_not()
    {
        List<int> release = [210_000, 305_000, 247_400];

        Assert.True(PriorityOf(Files(212, 301, 245), release) > PriorityOf(Files(150, 420, 95), release));
        Assert.Equal(PriorityOf(Files(212, 301, 245), []), PriorityOf(Files(150, 420, 95), []));
    }

    [Fact]
    public void track_lengths_survive_the_request_round_trip()
    {
        string json = JsonSerializer.Serialize(new SlskdSearchData("Muse", "The Resistance", false, false, 1, null, TrackDurations: [210_000, 305_000]));

        Assert.Equal([210_000, 305_000], SlskdSearchData.FromJson(json).TrackDurations);
    }

    private static int PriorityOf(List<SlskdFileData> files, List<int> releaseMs)
    {
        AlbumData album = Parser.CreateAlbumData(
            files.GroupBy(f => SlskdTextProcessor.GetMergedDirectoryKey(f.Filename)).Single(),
            new SlskdSearchData("Muse", "The Resistance", Interactive: true, ExpandDirectory: false,
                MinimumFiles: 1, MaximumFiles: null, TrackCount: 3, Tracks: [], TargetVariantTypes: null, AlbumType: "Album",
                TrackDurations: releaseMs),
            new SlskdFolderData(@"Music\Muse\The Resistance", "Muse", "The Resistance", "", "peer", true, 1_000_000, 0, [], 0, 0, 0, [], 0),
            new SlskdSettings(),
            expectedTrackCount: 3);

        return album.Priotity;
    }
}
