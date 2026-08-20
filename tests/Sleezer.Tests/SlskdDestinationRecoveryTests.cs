using System.Text.Json;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;
using Xunit;

namespace Sleezer.Tests;

// Covers the v1.11.3 regression found in the 2026-07-31 live audit: the
// release-derived batch destination was in-memory only, so a Lidarr restart
// mid-download left the item resolving to the PEER's source-directory name
// ("FLAC (24bit-96kHz)") while slskd had written to the batch destination
// ("Pantera - Reinventing the Steel"). The vanished-files guard then failed a
// perfectly healthy download.
public class SlskdDestinationRecoveryTests
{
    private static SlskdDownloadItem NewItem(params string[] filenames)
    {
        string source = "[" + string.Join(",", filenames.Select(f =>
            $"{{\"Filename\":{JsonSerializer.Serialize(f)},\"Size\":1000}}")) + "]";
        return new SlskdDownloadItem(new ReleaseInfo { Source = source, Title = "t", DownloadUrl = "u" });
    }

    private const string TransferJson = """
    {
      "batchId": "509d1c1b-9ba0-4ce4-b3fd-dd4d7c361a34",
      "id": "2fb7faec-1f94-4f40-8155-ce8ad67713c0",
      "username": "statiic",
      "direction": "Download",
      "filename": "media\\Pantera\\FLAC (24bit-96kHz)\\CD 01\\01 - Hellbound.flac",
      "size": 65485844,
      "state": "Completed, Succeeded"
    }
    """;

    [Fact]
    public void Transfer_records_carry_the_batch_id()
    {
        SlskdDownloadFile? file = SlskdDownloadFile.ParseSingle(JsonDocument.Parse(TransferJson).RootElement);

        Assert.NotNull(file);
        Assert.Equal("509d1c1b-9ba0-4ce4-b3fd-dd4d7c361a34", file!.BatchId);
    }

    [Fact]
    public void Every_file_in_a_batch_reports_the_same_batch_id()
    {
        string json = $"[{TransferJson},{TransferJson}]";
        List<SlskdDownloadFile> files = SlskdDownloadFile.GetFiles(JsonDocument.Parse(json).RootElement).ToList();

        Assert.Equal(2, files.Count);
        Assert.Single(files.Select(f => f.BatchId).Distinct());
        Assert.Equal("509d1c1b-9ba0-4ce4-b3fd-dd4d7c361a34",
            files.Select(f => f.BatchId).FirstOrDefault(id => !string.IsNullOrEmpty(id)));
    }

    [Fact]
    public void A_transfer_without_a_batch_id_parses_as_null()
    {
        SlskdDownloadFile? file = SlskdDownloadFile.ParseSingle(
            JsonDocument.Parse("""{"id":"x","filename":"a\\b.flac","size":1}""").RootElement);

        Assert.NotNull(file);
        Assert.Null(file!.BatchId);
    }

    [Fact]
    public void Without_a_known_subdirectory_a_multi_disc_item_resolves_to_the_peer_source_directory()
    {
        SlskdDownloadItem item = NewItem(
            @"@@x\media\Pantera\FLAC (24bit-96kHz)\CD 01\01 - Hellbound.flac",
            @"@@x\media\Pantera\FLAC (24bit-96kHz)\CD 02\01 - Avoid the Light.flac");

        Assert.True(item.IsMultiDirectory);
        Assert.Equal(
            Path.Combine("/downloads", "FLAC (24bit-96kHz)"),
            item.GetFullFolderPath(new NzbDrone.Common.Disk.OsPath("/downloads")).FullPath);
    }

    [Fact]
    public void A_confirmed_subdirectory_overrides_the_peer_source_directory()
    {
        SlskdDownloadItem item = NewItem(
            @"@@x\media\Pantera\FLAC (24bit-96kHz)\CD 01\01 - Hellbound.flac",
            @"@@x\media\Pantera\FLAC (24bit-96kHz)\CD 02\01 - Avoid the Light.flac");

        item.ConfirmedSubdirectory = "Pantera - Reinventing the Steel";

        Assert.Equal(
            Path.Combine("/downloads", "Pantera - Reinventing the Steel"),
            item.GetFullFolderPath(new NzbDrone.Common.Disk.OsPath("/downloads")).FullPath);
    }

    [Fact]
    public void A_lone_coincidental_match_never_wins_the_relocation()
    {
        (string, int)[] candidates = [("/downloads/Someone Elses Album", 1), ("/downloads/Unrelated", 0)];

        Assert.Null(SlskdPathResolver.SelectOwningFolder(candidates, 12));
    }

    [Fact]
    public void The_folder_holding_most_of_the_batch_wins()
    {
        (string, int)[] candidates =
        [
            ("/downloads/FLAC (24bit-96kHz)", 2),
            ("/downloads/Pantera - Reinventing the Steel", 10),
            ("/downloads/Unrelated", 0)
        ];

        Assert.Equal("/downloads/Pantera - Reinventing the Steel",
            SlskdPathResolver.SelectOwningFolder(candidates, 12));
    }

    [Theory]
    [InlineData(7, 12, "/downloads/Album")]   // a strict majority relocates
    [InlineData(6, 12, null)]                 // a 50/50 split has no unique owner
    [InlineData(5, 12, null)]
    [InlineData(1, 1, "/downloads/Album")]    // single-track release
    [InlineData(0, 0, null)]                  // nothing owned: never relocate
    public void Relocation_requires_most_of_the_batch(int matches, int owned, string? expected)
    {
        Assert.Equal(expected, SlskdPathResolver.SelectOwningFolder([("/downloads/Album", matches)], owned));
    }

    // Covers the AwaitingDiscMerge property only — the manager guard that reads
    // it lives behind IDiskProvider, which this project has no mocking for.
    [Fact]
    public void A_multi_disc_item_reports_awaiting_disc_merge_until_merged()
    {
        SlskdDownloadItem item = NewItem(
            @"@@x\media\Pantera\Album\CD 01\01 - Hellbound.flac",
            @"@@x\media\Pantera\Album\CD 02\01 - Avoid the Light.flac");

        Assert.True(item.IsMultiDirectory);
        Assert.True(item.AwaitingDiscMerge);

        // slskd pre-merged it via a batch destination, or the merge has run.
        item.DiscFoldersMerged = true;
        Assert.False(item.AwaitingDiscMerge);
    }

    [Fact]
    public void A_single_directory_item_never_awaits_a_disc_merge()
    {
        SlskdDownloadItem item = NewItem(@"@@x\media\Artist\Album\01 - One.flac");

        Assert.False(item.AwaitingDiscMerge);
    }

    [Theory]
    [InlineData("/data/downloads", "/data/downloads/Pantera - Reinventing the Steel", "Pantera - Reinventing the Steel")]
    [InlineData("/data/downloads/", "/data/downloads/Album/CD 1", "Album/CD 1")]
    [InlineData("/data/downloads", "/data/other/Album", null)]
    [InlineData("/data/downloads", "/data/downloads/../escape", null)]
    public void Relocation_relativizes_a_local_folder_against_the_local_root(string root, string folder, string? expected)
    {
        Assert.Equal(expected, SlskdPathResolver.MakeRelativeToDownloads(root, folder));
    }

    // Import destination for cue/log extras: the album folder Lidarr just wrote
    // the tracks into, derived from the imported track paths alone.
    private static string P(params string[] segments) => string.Join(Path.DirectorySeparatorChar, segments);

    [Fact]
    public void CommonParentDirectory_is_the_folder_the_tracks_share()
    {
        Assert.Equal(P("", "music", "Artist", "Album"), SlskdPathResolver.CommonParentDirectory(
            [P("", "music", "Artist", "Album", "01.flac"), P("", "music", "Artist", "Album", "02.flac")]));
    }

    [Fact]
    public void CommonParentDirectory_folds_disc_subfolders_into_the_album_folder()
    {
        Assert.Equal(P("", "music", "Album"), SlskdPathResolver.CommonParentDirectory(
            [P("", "music", "Album", "CD1", "01.flac"), P("", "music", "Album", "CD2", "01.flac")]));
    }

    [Fact]
    public void CommonParentDirectory_of_one_path_is_its_own_directory()
    {
        Assert.Equal(P("", "music", "Album"), SlskdPathResolver.CommonParentDirectory([P("", "music", "Album", "01.flac")]));
    }

    [Fact]
    public void CommonParentDirectory_is_null_without_paths()
    {
        Assert.Null(SlskdPathResolver.CommonParentDirectory([]));
    }

    [Fact]
    public void CommonParentDirectory_is_null_when_nothing_past_the_root_is_shared()
    {
        Assert.Null(SlskdPathResolver.CommonParentDirectory(
            [P("", "music", "A", "01.flac"), P("", "other", "B", "01.flac")]));
    }

    [Fact]
    public void CommonParentDirectory_is_null_for_bare_filenames()
    {
        Assert.Null(SlskdPathResolver.CommonParentDirectory(["01.flac", "02.flac"]));
    }
}
