using System.Text.Json;
using NLog;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;
using Xunit;

namespace Sleezer.Tests;

public sealed class SlskdTaggedFilesStoreTests : IDisposable
{
    private const string Renamed = "/downloads/Glass Harbour/02 - Lantern Song.flac";
    private const string Original = "/downloads/Glass Harbour/Moonward - Glass Harbour - 02 Lantern Song.flac";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sleezer-tagged-" + Guid.NewGuid().ToString("N"));

    // Where an unchecked "../" id would write: next to the folder, so the test removes it if a broken build does.
    private readonly string _escape = "escape-" + Guid.NewGuid().ToString("N");

    private SlskdTaggedFilesStore Store => new(_folder, LogManager.GetLogger("tests"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
        File.Delete(Path.Combine(Path.GetTempPath(), _escape + ".json"));
    }

    private static SlskdDownloadItem NewItem(string id = "0a1b2c3d-r2")
    {
        string source = $"[{{\"Filename\":{JsonSerializer.Serialize(@"@@x\Glass Harbour\Moonward - Glass Harbour - 02 Lantern Song.flac")},\"Size\":45814441}}]";
        return new SlskdDownloadItem(new ReleaseInfo { Source = source, Title = "t", DownloadUrl = "u" }) { ID = id };
    }

    [Fact]
    public void a_rebuilt_grab_still_owns_the_file_the_tagger_renamed()
    {
        SlskdDownloadItem before = NewItem();
        before.RecordTaggedFile(Renamed, 45_958_776, Original);
        Store.Save(before.ID, before.TaggedFiles);

        SlskdDownloadItem after = NewItem();
        Assert.False(after.BuildOwnedFiles().Owns("02 - Lantern Song.flac", 45_958_776));

        after.RestoreTaggedFiles(Store.Load(after.ID));
        Assert.True(after.BuildOwnedFiles().Owns("02 - Lantern Song.flac", 45_958_776));
    }

    [Fact]
    public void another_grabs_record_is_not_loaded()
    {
        SlskdDownloadItem item = NewItem("aaa111");
        item.RecordTaggedFile(Renamed, 45_958_776, Original);
        Store.Save(item.ID, item.TaggedFiles);

        Assert.Empty(Store.Load("bbb222"));
    }

    [Fact]
    public void a_download_id_that_could_name_another_path_is_never_used()
    {
        Store.Save("../" + _escape, [new SlskdTaggedFile(Renamed, 1, true)]);

        Assert.False(File.Exists(Path.Combine(_folder, "..", _escape + ".json")));
        Assert.Empty(Store.Load("../" + _escape));
    }

    [Fact]
    public void an_unreadable_record_claims_nothing()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "0a1b2c3d-r2.json"), "not json");

        Assert.Empty(Store.Load("0a1b2c3d-r2"));
    }

    [Fact]
    public void a_deleted_record_is_gone()
    {
        Store.Save("0a1b2c3d-r2", [new SlskdTaggedFile(Renamed, 1, true)]);
        Store.Delete("0a1b2c3d-r2");

        Assert.Empty(Store.Load("0a1b2c3d-r2"));
    }

    [Fact]
    public void a_file_tagged_again_since_the_restart_keeps_its_newer_size()
    {
        SlskdDownloadItem item = NewItem();
        item.RecordTaggedFile(Renamed, 46_000_000, Original);

        item.RestoreTaggedFiles([new SlskdTaggedFile(Renamed, 45_958_776, true)]);

        Assert.True(item.BuildOwnedFiles().Owns("02 - Lantern Song.flac", 46_000_000));
        Assert.False(item.BuildOwnedFiles().Owns("02 - Lantern Song.flac", 45_958_776));
    }
}
