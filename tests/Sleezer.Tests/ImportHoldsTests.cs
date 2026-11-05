using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Download;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer;
using NzbDrone.Plugin.Sleezer.Core.PostProcessing;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using Xunit;

namespace Sleezer.Tests;

public class ImportHoldsTests : IDisposable
{
    private static readonly Logger Log = LogManager.CreateNullLogger();
    private readonly string _appData = Path.Combine(Path.GetTempPath(), "import-holds-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_appData))
            Directory.Delete(_appData, true);
    }

    private sealed class AppFolder(string appData) : IAppFolderInfo
    {
        public string AppDataFolder => appData;
        public string TempFolder => Path.GetTempPath();
        public string StartUpFolder => appData;
    }

    private ImportHolds NewHolds() => new(new AppFolder(_appData), Log);

    private string HoldsFolder => Directory.EnumerateDirectories(_appData, "import-holds", SearchOption.AllDirectories).Single();

    [Fact]
    public void A_hold_outlives_a_restart_until_it_is_released()
    {
        NewHolds().Hold("abc-123", "fingerprint says 01.flac is a different recording than 'Lantern Song'");

        ImportHolds afterRestart = NewHolds();
        Assert.Equal("fingerprint says 01.flac is a different recording than 'Lantern Song'", afterRestart.ReasonFor("abc-123"));
        Assert.Null(afterRestart.ReasonFor("def-456"));

        afterRestart.Release("abc-123");
        Assert.Null(NewHolds().ReasonFor("abc-123"));
    }

    // Held, but never written: the id would name a file elsewhere.
    [Theory]
    [InlineData("")]
    [InlineData("../escape")]
    [InlineData("a/b")]
    public void An_id_that_could_name_another_path_is_held_in_memory_only(string downloadId)
    {
        ImportHolds holds = NewHolds();
        holds.Hold(downloadId, "reason");

        Assert.Equal("reason", holds.ReasonFor(downloadId));
        Assert.Null(NewHolds().ReasonFor(downloadId));
        Assert.False(Directory.Exists(_appData) && Directory.EnumerateFiles(_appData, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public void A_hold_that_cannot_be_saved_still_holds_until_a_restart()
    {
        string folder = Path.Combine(PluginFolder.Resolve(Path.Combine(_appData, "plugins"), PluginInfo.RepoUrl), "import-holds");
        Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
        File.WriteAllText(folder, "a file where the folder should be");
        ImportHolds holds = NewHolds();

        holds.Hold("abc-123", "reason");

        Assert.Equal("reason", holds.ReasonFor("abc-123"));
        Assert.Null(NewHolds().ReasonFor("abc-123"));
        holds.Release("abc-123");
        Assert.Null(holds.ReasonFor("abc-123"));
    }

    // A permission problem is the real shape of it; Windows has no file mode to take away.
    [Fact]
    public void A_hold_that_cannot_be_read_still_holds()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "no Unix file mode");
        ImportHolds holds = NewHolds();
        holds.Hold("abc-123", "reason");
        File.SetUnixFileMode(Path.Combine(HoldsFolder, "abc-123.txt"), UnixFileMode.None);

        Assert.NotNull(NewHolds().ReasonFor("abc-123"));
    }

    [Fact]
    public void A_hold_in_a_folder_that_cannot_be_listed_still_holds()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "no Unix file mode");
        NewHolds().Hold("abc-123", "reason");
        string folder = HoldsFolder;
        File.SetUnixFileMode(folder, UnixFileMode.None);
        try
        {
            Assert.NotNull(NewHolds().ReasonFor("abc-123"));
        }
        finally
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // A retry's id carries "-r2"; the check must refuse it through the real store, and only it.
    [Fact]
    public void A_retry_ids_hold_refuses_only_that_retry()
    {
        ImportHolds holds = NewHolds();
        holds.Hold("0123abcdef-r2", "fingerprint says 01.flac is a different recording than 'Lantern Song'");
        ImportHoldSpecification spec = new(holds);

        Assert.False(spec.IsSatisfiedBy(new LocalAlbumRelease([]), new DownloadClientItem { DownloadId = "0123abcdef-r2" }).Accepted);
        Assert.True(spec.IsSatisfiedBy(new LocalAlbumRelease([]), new DownloadClientItem { DownloadId = "0123abcdef" }).Accepted);
    }

    private sealed class FakeHolds(string id, string reason) : IImportHolds
    {
        public void Hold(string downloadId, string why) => throw new NotSupportedException();
        public string? ReasonFor(string downloadId) => downloadId == id ? reason : null;
        public void Release(string downloadId) => throw new NotSupportedException();
    }

    [Fact]
    public void The_held_download_is_refused_with_its_reason()
    {
        ImportHoldSpecification spec = new(new FakeHolds("abc-123", "fingerprint says 01.flac is a different recording than 'Lantern Song'"));

        var decision = spec.IsSatisfiedBy(new LocalAlbumRelease([]), new DownloadClientItem { DownloadId = "abc-123" });

        Assert.False(decision.Accepted);
        Assert.Equal("Held for a manual import: fingerprint says 01.flac is a different recording than 'Lantern Song'", decision.Reason);
    }

    [Theory]
    [InlineData("def-456")]
    [InlineData("")]
    [InlineData(null)]
    public void Any_other_download_is_accepted(string? downloadId)
    {
        ImportHoldSpecification spec = new(new FakeHolds("abc-123", "held"));

        Assert.True(spec.IsSatisfiedBy(new LocalAlbumRelease([]), new DownloadClientItem { DownloadId = downloadId }).Accepted);
        Assert.True(spec.IsSatisfiedBy(new LocalAlbumRelease([]), null!).Accepted);
    }

    private static readonly IReadOnlySet<string> Owned = new HashSet<string> { "/dl/Album/01.flac" };

    [Fact]
    public void Only_a_verified_refusal_of_this_downloads_own_file_holds_it()
    {
        PreImportTagger.TaggingResult refused = new(0, 0, 0, WrongRecording: "fingerprint says 01.flac is a different recording than 'Lantern Song'", WrongRecordingFile: "/dl/Album/01.flac");

        Assert.Equal(refused.WrongRecording, ImportHolds.ReasonToHold(refused, verifying: true, Owned));
        Assert.Null(ImportHolds.ReasonToHold(refused, verifying: false, Owned));
        Assert.Null(ImportHolds.ReasonToHold(refused with { WrongRecordingFile = "/dl/Album/other-grab.flac" }, verifying: true, Owned));
        Assert.Null(ImportHolds.ReasonToHold(refused with { WrongRecordingFile = null }, verifying: true, Owned));
        Assert.Null(ImportHolds.ReasonToHold(new(5, 0, 0), verifying: true, Owned));
    }
}
