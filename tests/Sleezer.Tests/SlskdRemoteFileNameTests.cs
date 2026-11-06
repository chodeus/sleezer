using System.Text.Json;
using NLog;
using NzbDrone.Core.Download;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Core.Model;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek;
using Xunit;

namespace Sleezer.Tests;

// Peers send backslash paths, and Path.GetFileName splits only on '/' outside Windows.
public class SlskdRemoteFileNameTests
{
    private const string Dir = @"@@peer\Music\David Guetta\[2010] The Best Of";
    private const string Track = "1-10 - Love Is Gone.mp3";
    private static readonly SlskdItemsParser Parser = new(LogManager.GetLogger("tests"));

    [Theory]
    [InlineData(@"@@peer\Music\Album\01 - Song.flac", "01 - Song.flac")]
    [InlineData("@@peer/Music/Album/01 - Song.flac", "01 - Song.flac")]
    [InlineData(@"@@peer\Music/Album\01 - Song.flac", "01 - Song.flac")]
    [InlineData("01 - Song.flac", "01 - Song.flac")]
    [InlineData(@"@@peer\Music\Album\", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void The_file_name_is_the_last_segment_after_either_separator(string? remote, string expected) =>
        Assert.Equal(expected, SlskdTextProcessor.GetFileNameFromFilename(remote));

    [Fact]
    public void A_downloaded_file_maps_back_to_its_transfer()
    {
        SlskdDownloadItem item = ItemWith((Track, "Completed, Succeeded"), ("1-11 - Other.mp3", "Completed, Succeeded"));

        SlskdFileState? state = item.FileStateForLocalName("1-10 - love is gone.mp3");

        Assert.NotNull(state);
        Assert.Equal(Dir + "\\" + Track, state.File.Filename);
        Assert.Null(item.FileStateForLocalName("1-12 - Missing.mp3"));
    }

    [Fact]
    public void The_failed_download_message_names_the_file_not_the_peer_path()
    {
        SlskdDownloadItem item = ItemWith((Track, "Completed, Errored"));
        item.FileStates[Dir + "\\" + Track].MarkRetriesExhausted();

        SlskdStatusResolver.DownloadStatus resolved = SlskdStatusResolver.Resolve(item, TimeSpan.FromMinutes(30), DateTime.UtcNow);

        Assert.Equal(DownloadItemStatus.Failed, resolved.Status);
        Assert.Equal($"Downloading 1 files failed: {Track}", resolved.Message);
    }

    [Fact]
    public void A_track_number_gap_costs_a_backslash_folder_what_it_costs_a_slash_one()
    {
        string[] gapped = ["01", "02", "03", "05", "06"];

        Assert.Equal(Priority('/', gapped), Priority('\\', gapped));
        Assert.True(Priority('/', gapped) < Priority('/', ["01", "02", "03", "04", "05"]));
    }

    private static SlskdDownloadItem ItemWith(params (string Name, string State)[] files)
    {
        string source = "[" + string.Join(",", files.Select(f =>
            $"{{\"Filename\":{JsonSerializer.Serialize(Dir + "\\" + f.Name)},\"Size\":1000}}")) + "]";
        SlskdDownloadItem item = new(new ReleaseInfo { Source = source, Title = "t", DownloadUrl = "u" });
        item.SlskdDownloadDirectory = new SlskdDownloadDirectory(Dir, files.Length, [.. files.Select(f => Transfer(f.Name, f.State))]);
        return item;
    }

    private static SlskdDownloadFile Transfer(string name, string state) => new(
        Id: name, Username: "peer", Direction: "Download", Filename: Dir + "\\" + name,
        Size: 1000, StartOffset: 0, State: state,
        RequestedAt: DateTime.UtcNow, EnqueuedAt: DateTime.UtcNow, StartedAt: DateTime.UtcNow,
        BytesTransferred: 1000, AverageSpeed: 0, BytesRemaining: 0,
        ElapsedTime: TimeSpan.Zero, PercentComplete: 100, RemainingTime: TimeSpan.Zero, EndedAt: null);

    private static int Priority(char separator, string[] numbers)
    {
        string folder = string.Join(separator, "Music", "Some Artist - Some Album");
        string[] titles = ["Alpha", "Bravo", "Charlie", "Delta", "Echo"];
        IGrouping<string, SlskdFileData> directory = numbers
            .Select((n, i) => new SlskdFileData($"{folder}{separator}{n} - {titles[i]}.flac", null, 16, 30_000_000, 300, ".flac", 44100, 0, false))
            .GroupBy(f => SlskdTextProcessor.GetMergedDirectoryKey(f.Filename))
            .Single();

        AlbumData album = Parser.CreateAlbumData(
            directory,
            new SlskdSearchData("Some Artist", "Some Album", Interactive: false, ExpandDirectory: false,
                MinimumFiles: 1, MaximumFiles: 40, TrackCount: 5, Tracks: [.. titles],
                TargetVariantTypes: null, AlbumType: "Album"),
            new SlskdFolderData(folder, "", "", "", "peer", true, 1_000_000, 0, [], 0, 0, 0, [], 0),
            new SlskdSettings(),
            expectedTrackCount: 5);
        return album.Priotity;
    }
}
