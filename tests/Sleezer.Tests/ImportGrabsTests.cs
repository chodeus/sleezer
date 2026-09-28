using NzbDrone.Core.History;
using NzbDrone.Plugin.Sleezer.Metadata.ScheduledTasks.SearchSniper;
using Xunit;

namespace Sleezer.Tests;

// A store copy is named for the release it landed on, so Search Sniper also scores the grab each file came from.
public class ImportGrabsTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static EntityHistory Import(int fileId, string? downloadId, int minutes = 0, string key = "fileId") =>
        new()
        {
            EventType = EntityHistoryEventType.TrackFileImported,
            DownloadId = downloadId,
            Date = Start.AddMinutes(minutes),
            Data = new Dictionary<string, string> { [key] = fileId.ToString() }
        };

    private static EntityHistory Grab(string downloadId, string title, int minutes = 0) =>
        new() { EventType = EntityHistoryEventType.Grabbed, DownloadId = downloadId, SourceTitle = title, Date = Start.AddMinutes(minutes) };

    [Fact]
    public void A_file_maps_to_the_download_its_import_recorded()
    {
        Dictionary<int, string> downloads = ImportGrabs.DownloadIds([7], [Import(7, "dl-a"), Import(8, "dl-b")]);

        Assert.Equal("dl-a", Assert.Single(downloads).Value);
    }

    [Theory]
    [InlineData("fileId")]
    [InlineData("FileId")]
    public void The_file_id_key_is_read_in_either_case(string key)
    {
        Assert.Equal("dl-a", ImportGrabs.DownloadIds([7], [Import(7, "dl-a", key: key)])[7]);
    }

    [Fact]
    public void An_import_with_no_download_has_no_grab()
    {
        Assert.Empty(ImportGrabs.DownloadIds([7], [Import(7, null)]));
    }

    [Fact]
    public void A_file_imported_twice_keeps_its_latest_download()
    {
        Assert.Equal("dl-new", ImportGrabs.DownloadIds([7], [Import(7, "dl-new", 60), Import(7, "dl-old", 0)])[7]);
    }

    [Fact]
    public void A_file_takes_the_latest_grab_of_its_download()
    {
        Dictionary<int, EntityHistory> grabs = ImportGrabs.Of(new() { [7] = "dl-a" }, [Grab("dl-a", "second", 30), Grab("dl-a", "first", 0), Grab("dl-b", "other", 60)]);

        Assert.Equal("second", grabs[7].SourceTitle);
    }

    [Fact]
    public void A_file_whose_download_has_no_grab_is_left_out()
    {
        Assert.Empty(ImportGrabs.Of(new() { [7] = "dl-a" }, [Grab("dl-b", "other")]));
    }
}
