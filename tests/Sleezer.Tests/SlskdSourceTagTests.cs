using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;
using Xunit;

namespace Sleezer.Tests;

// Tag Files as Soulseek (SlskdProviderSettings): SlskdSourceTag.Write.
public class SlskdSourceTagTests : IDisposable
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sleezer-slskd-src-" + Path.GetRandomFileName());

    public SlskdSourceTagTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Flac(string name, string? peerSource = null)
    {
        string path = Path.Combine(_dir, name);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "silence.flac"), path);
        if (peerSource != null)
            Assert.True(SourceTagWriter.TryWrite(path, peerSource, Log));
        return path;
    }

    // The fixture's 8 KiB padding block absorbs a tag write; without it the file must grow.
    private string FlacWithoutPadding(string name)
    {
        byte[] d = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "silence.flac"));
        List<byte[]> blocks = [];
        int i = 4;
        bool last;
        do
        {
            last = (d[i] & 0x80) != 0;
            int length = (d[i + 1] << 16) | (d[i + 2] << 8) | d[i + 3];
            if ((d[i] & 0x7f) != 1)
                blocks.Add(d[i..(i + 4 + length)]);
            i += 4 + length;
        }
        while (!last);

        using MemoryStream o = new();
        o.Write(d, 0, 4);
        for (int k = 0; k < blocks.Count; k++)
        {
            blocks[k][0] = (byte)((blocks[k][0] & 0x7f) | (k == blocks.Count - 1 ? 0x80 : 0));
            o.Write(blocks[k]);
        }
        o.Write(d, i, d.Length - i);

        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, o.ToArray());
        return path;
    }

    private static SlskdDownloadItem Owning(params string[] paths)
    {
        string source = "[" + string.Join(",", paths.Select(p =>
            $"{{\"Filename\":{JsonSerializer.Serialize(@"@@u\Artist\Album\" + Path.GetFileName(p))},\"Size\":{new FileInfo(p).Length}}}")) + "]";
        return new SlskdDownloadItem(new ReleaseInfo { Source = source, Title = "t", DownloadUrl = "u" });
    }

    private static string? ReadSource(string path)
    {
        using TagLib.File f = TagLib.File.Create(path);
        var xiph = (TagLib.Ogg.XiphComment?)f.GetTag(TagLib.TagTypes.Xiph, false);
        return xiph?.GetFirstField("SOURCE");
    }

    [Fact]
    public void replaces_the_shared_files_store_url_with_soulseek()
    {
        string path = Flac("01 Lantern Song.flac", peerSource: "https://open.qobuz.com/album/abc123");

        int written = SlskdSourceTag.Write(Owning(path), [path], Log, CancellationToken.None);

        Assert.Equal(1, written);
        Assert.Equal("soulseek", ReadSource(path));
    }

    [Fact]
    public void the_download_still_owns_the_file_after_the_tag_changes_its_size()
    {
        string path = FlacWithoutPadding("01 Lantern Song.flac");
        long before = new FileInfo(path).Length;
        SlskdDownloadItem item = Owning(path);

        SlskdSourceTag.Write(item, [path], Log, CancellationToken.None);

        long after = new FileInfo(path).Length;
        Assert.NotEqual(before, after);
        Assert.Equal(after, item.BuildOwnedFileSizes()["01 Lantern Song.flac"]);
    }

    [Fact]
    public void a_file_another_download_left_in_the_folder_is_not_touched()
    {
        string ours = Flac("01 Lantern Song.flac");
        string theirs = Flac("02 Tidewater.flac", peerSource: "https://www.deezer.com/album/9");

        int written = SlskdSourceTag.Write(Owning(ours), [ours, theirs], Log, CancellationToken.None);

        Assert.Equal(1, written);
        Assert.Equal("https://www.deezer.com/album/9", ReadSource(theirs));
    }

    [Fact]
    public void a_file_slskd_rejected_at_enqueue_is_not_touched()
    {
        string path = Flac("01 Lantern Song.flac", peerSource: "https://www.deezer.com/album/9");
        SlskdDownloadItem item = Owning(path);
        item.MarkEnqueueFailed([@"@@u\Artist\Album\01 Lantern Song.flac"]);

        Assert.Equal(0, SlskdSourceTag.Write(item, [path], Log, CancellationToken.None));
        Assert.Equal("https://www.deezer.com/album/9", ReadSource(path));
    }

    [Fact]
    public void a_same_named_file_of_another_size_is_not_touched()
    {
        string path = Flac("01 Lantern Song.flac");
        SlskdDownloadItem item = Owning(path);
        File.AppendAllText(path, "x");

        Assert.Equal(0, SlskdSourceTag.Write(item, [path], Log, CancellationToken.None));
        Assert.Null(ReadSource(path));
    }

    [Fact]
    public void a_file_that_cannot_take_the_tag_is_skipped_without_failing_the_download()
    {
        // Two silent MPEG-1 Layer III frames (128 kbps, 44.1 kHz, 417 bytes each): no Xiph comment to hold SOURCE.
        byte[] frames = new byte[834];
        foreach (int start in new[] { 0, 417 })
            new byte[] { 0xFF, 0xFB, 0x90, 0x00 }.CopyTo(frames, start);
        string mp3 = Path.Combine(_dir, "03 Salt Road.mp3");
        File.WriteAllBytes(mp3, frames);
        string gone = Flac("04 Undertow.flac");
        SlskdDownloadItem item = Owning(mp3, gone);
        File.Delete(gone);

        Assert.Equal(0, SlskdSourceTag.Write(item, [mp3, gone], Log, CancellationToken.None));
        Assert.Equal(frames, File.ReadAllBytes(mp3));
    }

    [Fact]
    public void the_post_processing_timeout_stops_the_pass_before_the_next_file()
    {
        string path = Flac("01 Lantern Song.flac");
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => SlskdSourceTag.Write(Owning(path), [path], Log, cts.Token));
        Assert.Null(ReadSource(path));
    }

    [Fact]
    public void the_setting_is_off_by_default()
    {
        Assert.False(new SlskdProviderSettings().TagFilesAsSoulseek);
    }
}
