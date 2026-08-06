using System.Text.Json;
using NLog;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Core.Model;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek;
using Xunit;

namespace Sleezer.Tests;

// Covers the 2026-08-06 live-audit fixes: a folder year fabricated a Jan-1
// publish date that Lidarr's EarlyReleaseSpecification permanently rejected
// (every well-named "(2017)" source lost to a mid-2017 album), the rejected
// partial source then won the grab anyway, and the pre-import tagger's
// rename/rewrite made its file unclaimable by the ownership guard, poisoning
// every retry that reused the pinned album folder.
public class AlbumDataPublishDateTests
{
    [Fact]
    public void Year_precision_reports_discovery_time_not_jan_1()
    {
        DateTime before = DateTime.UtcNow.AddSeconds(-5);
        AlbumData data = new("Slskd", "SoulseekDownloadProtocol")
        {
            ArtistName = "GLXY",
            AlbumName = "Proposition Mind Less",
            ReleaseDatePrecision = "year",
            ReleaseDateTime = new DateTime(2017, 1, 1),
        };

        ReleaseInfo release = data.ToReleaseInfo();

        Assert.True(release.PublishDate >= before);
        Assert.NotEqual(new DateTime(2017, 1, 1), release.PublishDate);
        Assert.Contains("(2017)", release.Title);
    }

    [Fact]
    public void Day_precision_keeps_the_real_publish_date()
    {
        AlbumData data = new("Deezer", "p")
        {
            ReleaseDatePrecision = "day",
            ReleaseDateTime = new DateTime(2027, 9, 1),
        };

        Assert.Equal(new DateTime(2027, 9, 1), data.ToReleaseInfo().PublishDate);
    }

    [Fact]
    public void No_date_at_all_reports_discovery_time()
    {
        DateTime before = DateTime.UtcNow.AddSeconds(-5);

        Assert.True(new AlbumData("Slskd", "p").ToReleaseInfo().PublishDate >= before);
    }

    [Fact]
    public void Day_precision_without_a_parsed_date_falls_back_to_discovery_time()
    {
        DateTime before = DateTime.UtcNow.AddSeconds(-5);
        AlbumData data = new("SubSonic", "p") { ReleaseDatePrecision = "day" };

        Assert.True(data.ToReleaseInfo().PublishDate >= before);
    }
}

public class SlskdCoherentSourceClassificationTests
{
    private static readonly SlskdItemsParser Parser = new(LogManager.GetLogger("tests"));

    private static IGrouping<string, SlskdFileData> Group(params string[] filenames) =>
        filenames
            .Select(f => new SlskdFileData(f, null, 16, 30_000_000, 300, ".flac", 44100, 0, false))
            .GroupBy(f => SlskdTextProcessor.GetMergedDirectoryKey(f.Filename))
            .Single();

    private static SlskdFolderData Folder(string path, string year = "") =>
        new(path, "", "", year, "peer", true, 1_000_000, 0, [], 0, 0, 0, [], 0);

    private static readonly SlskdSearchData PropositionSearch = new(
        "GLXY", "Proposition Mind Less", Interactive: false, ExpandDirectory: false,
        MinimumFiles: 1, MaximumFiles: 4, TrackCount: 2,
        Tracks: ["Proposition", "Mind Less"], TargetVariantTypes: null, AlbumType: "Single");

    // The live 2026-08-06 grab: a folder holding only two versions of the SAME
    // track ("Mind Less" + its Radio Edit) and no "Proposition" at all.
    [Fact]
    public void A_folder_covering_one_of_two_tracks_is_not_a_match_when_coherence_is_required()
    {
        AlbumData album = Parser.CreateAlbumData(
            "search1",
            Group(
                @"Music\Pilot Records\PILOT027 - GLXY - Proposition EP\2. GLXY - Mind Less (Radio Edit).flac",
                @"Music\Pilot Records\PILOT027 - GLXY - Proposition EP\4. GLXY - Mind Less.flac"),
            PropositionSearch,
            Folder(@"Music\Pilot Records\PILOT027 - GLXY - Proposition EP"),
            new SlskdSettings { RequireCoherentSingleSource = true },
            expectedTrackCount: 2);

        Assert.False(album.MatchedSearchCriteria);
    }

    [Fact]
    public void A_folder_covering_both_tracks_stays_a_match()
    {
        AlbumData album = Parser.CreateAlbumData(
            "search1",
            Group(
                @"Music\Pilot Records\PILOT027 - GLXY - Proposition EP\1. GLXY - Proposition.flac",
                @"Music\Pilot Records\PILOT027 - GLXY - Proposition EP\4. GLXY - Mind Less.flac"),
            PropositionSearch,
            Folder(@"Music\Pilot Records\PILOT027 - GLXY - Proposition EP"),
            new SlskdSettings { RequireCoherentSingleSource = true },
            expectedTrackCount: 2);

        Assert.True(album.MatchedSearchCriteria);
    }

    [Fact]
    public void A_slskd_folder_year_is_year_precision_and_never_a_publish_date()
    {
        DateTime before = DateTime.UtcNow.AddSeconds(-5);

        AlbumData album = Parser.CreateAlbumData(
            "search1",
            Group(
                @"complete\Pilot\[PILOT027] GLXY - Proposition # Mind Less (2017)\1. GLXY - Proposition.flac",
                @"complete\Pilot\[PILOT027] GLXY - Proposition # Mind Less (2017)\4. GLXY - Mind Less.flac"),
            PropositionSearch,
            Folder(@"complete\Pilot\[PILOT027] GLXY - Proposition # Mind Less (2017)", year: "2017"),
            new SlskdSettings { RequireCoherentSingleSource = true },
            expectedTrackCount: 2);

        Assert.Equal("year", album.ReleaseDatePrecision);
        Assert.Equal(new DateTime(2017, 1, 1), album.ReleaseDateTime);

        ReleaseInfo release = album.ToShareInfo();
        Assert.True(release.PublishDate >= before);
        Assert.Contains("(2017)", release.Title);
    }
}

public class SlskdOwnershipOverlayTests
{
    private static SlskdDownloadItem NewItem(params (string Name, long Size)[] files)
    {
        string source = "[" + string.Join(",", files.Select(f =>
            $"{{\"Filename\":{JsonSerializer.Serialize(f.Name)},\"Size\":{f.Size}}}")) + "]";
        return new SlskdDownloadItem(new ReleaseInfo { Source = source, Title = "t", DownloadUrl = "u" });
    }

    [Fact]
    public void Owned_sizes_start_from_the_enqueued_identities()
    {
        SlskdDownloadItem item = NewItem(
            (@"@@x\PILOT027 - GLXY - Proposition EP\2. GLXY - Mind Less (Radio Edit).flac", 24_897_013),
            (@"@@x\PILOT027 - GLXY - Proposition EP\4. GLXY - Mind Less.flac", 39_281_821));

        Dictionary<string, long> owned = item.BuildOwnedFileSizes();

        Assert.Equal(24_897_013, owned["2. GLXY - Mind Less (Radio Edit).flac"]);
        Assert.Equal(39_281_821, owned["4. GLXY - Mind Less.flac"]);
    }

    // The live poison chain: the tagger renamed "4. GLXY - Mind Less.flac" to
    // "02 - Mind Less.flac" and the tag write grew it by ~31 KB, so the
    // basename+size guard could no longer claim it and retained it forever.
    [Fact]
    public void A_tagged_rename_adds_a_claimable_identity()
    {
        SlskdDownloadItem item = NewItem(
            (@"@@x\PILOT027 - GLXY - Proposition EP\4. GLXY - Mind Less.flac", 39_281_821));

        item.RecordTaggedFile("02 - Mind Less.flac", 39_312_677);
        Dictionary<string, long> owned = item.BuildOwnedFileSizes();

        Assert.Equal(39_312_677, owned["02 - Mind Less.flac"]);
        Assert.Equal(39_281_821, owned["4. GLXY - Mind Less.flac"]);
    }

    [Fact]
    public void An_in_place_tag_write_overrides_the_enqueued_size()
    {
        SlskdDownloadItem item = NewItem((@"@@x\d\track.flac", 100));

        item.RecordTaggedFile("track.flac", 130);

        Assert.Equal(130, item.BuildOwnedFileSizes()["track.flac"]);
    }

    [Fact]
    public void Blank_basenames_are_ignored()
    {
        SlskdDownloadItem item = NewItem((@"@@x\d\track.flac", 100));

        item.RecordTaggedFile("", 5);

        Assert.Single(item.BuildOwnedFileSizes());
    }
}

public class TerminalDownloadEventTests
{
    [Theory]
    [InlineData(DownloadHistoryEventType.DownloadFailed, true)]
    [InlineData(DownloadHistoryEventType.DownloadIgnored, true)]
    [InlineData(DownloadHistoryEventType.DownloadImported, true)]
    [InlineData(DownloadHistoryEventType.DownloadImportIncomplete, false)]
    [InlineData(DownloadHistoryEventType.DownloadGrabbed, false)]
    public void Terminal_set_is_exactly_failed_ignored_imported(DownloadHistoryEventType eventType, bool expected)
    {
        Assert.Equal(expected, SlskdDownloadItem.IsTerminalDownloadEvent(eventType));
    }

    [Fact]
    public void No_history_is_not_terminal()
    {
        Assert.False(SlskdDownloadItem.IsTerminalDownloadEvent(null));
    }
}
