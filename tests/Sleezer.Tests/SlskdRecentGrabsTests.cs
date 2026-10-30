using System.Text.Json;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;
using Xunit;

namespace Sleezer.Tests;

// The recent grabs a folder is matched against are read in full once a minute; each call adds only the grabs written since.
public class SlskdRecentGrabsTests
{
    private const string Lantern = @"@@x\Glass Harbour\01 Lantern Song.flac";
    private static readonly DateTime Start = new(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc);

    private static DownloadHistory Grab(string downloadId, string file, DateTime date) => new()
    {
        DownloadId = downloadId,
        Date = date,
        Release = new ReleaseInfo { Source = $"[{{\"Filename\":{JsonSerializer.Serialize(file)},\"Size\":1000}}]", Title = "t", DownloadUrl = "u" },
    };

    private sealed class History
    {
        public List<DownloadHistory> Rows { get; } = [];
        public List<DateTime> Reads { get; } = [];
        public DateTime Now { get; set; } = Start;

        public SlskdRecentGrabs Grabs() => new(since =>
        {
            Reads.Add(since);
            return [.. Rows.Where(r => r.Date >= since).OrderByDescending(r => r.Date)];
        }, () => Now);
    }

    [Fact]
    public void the_whole_window_is_read_once_a_minute()
    {
        History history = new();
        history.Rows.Add(Grab("a", Lantern, Start.AddDays(-3)));
        SlskdRecentGrabs grabs = history.Grabs();

        foreach (int seconds in new[] { 0, 20, 40, 60 })
        {
            history.Now = Start.AddSeconds(seconds);
            grabs.Owning(Lantern);
        }

        Assert.Equal([Start.AddDays(-14), Start.AddSeconds(-60), Start.AddSeconds(-40), Start.AddSeconds(60).AddDays(-14)], history.Reads);
    }

    [Fact]
    public void a_grab_written_after_the_read_is_found_on_the_next_call_ahead_of_older_ones()
    {
        History history = new();
        history.Rows.Add(Grab("older", Lantern, Start.AddDays(-3)));
        SlskdRecentGrabs grabs = history.Grabs();
        grabs.Owning(Lantern);

        history.Rows.Add(Grab("newer", Lantern, Start.AddSeconds(-1)));
        history.Now = Start.AddSeconds(3);

        Assert.Equal(["newer", "older"], grabs.Owning(Lantern).Select(g => g.DownloadId));
    }

    [Fact]
    public void a_grab_read_again_is_listed_once()
    {
        History history = new();
        history.Rows.Add(Grab("a", Lantern, Start.AddSeconds(-5)));
        SlskdRecentGrabs grabs = history.Grabs();

        grabs.Owning(Lantern);
        history.Now = Start.AddSeconds(3);
        grabs.Owning(Lantern);
        history.Now = Start.AddSeconds(6);

        Assert.Equal(["a"], grabs.Owning(Lantern).Select(g => g.DownloadId));
    }

    [Fact]
    public void only_grabs_holding_the_file_are_returned_newest_first()
    {
        History history = new();
        history.Rows.Add(Grab("newer", Lantern, Start.AddDays(-1)));
        history.Rows.Add(Grab("other", @"@@x\Driftwood\01 Tide.flac", Start.AddDays(-2)));
        history.Rows.Add(Grab("older", Lantern, Start.AddDays(-3)));

        Assert.Equal(["newer", "older"], history.Grabs().Owning(Lantern).Select(g => g.DownloadId));
    }

    [Fact]
    public void a_grab_whose_files_cannot_be_read_owns_nothing()
    {
        History history = new();
        history.Rows.Add(new DownloadHistory { DownloadId = "broken", Date = Start.AddDays(-1), Release = new ReleaseInfo { Source = "not json", Title = "t", DownloadUrl = "u" } });
        history.Rows.Add(Grab("a", Lantern, Start.AddDays(-2)));

        Assert.Equal(["a"], history.Grabs().Owning(Lantern).Select(g => g.DownloadId));
    }
}
