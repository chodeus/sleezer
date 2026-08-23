using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using Xunit;

namespace Sleezer.Tests;

// smoked-salmon reported "no encoder or source markers in the tags" on Qobuz downloads:
// the source URL was in hand while tagging and then discarded. Lidarr's AudioTag has no
// URL field, so unlike title/artist/media this one survives writeAudioTags=sync.
public class SourceTagWriterTests : IDisposable
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sleezer-src-" + Path.GetRandomFileName());

    public SourceTagWriterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Fixture()
    {
        string src = Path.Combine(AppContext.BaseDirectory, "Fixtures", "silence.flac");
        string dst = Path.Combine(_dir, "track.flac");
        File.Copy(src, dst);
        return dst;
    }

    private static string? ReadSource(string path)
    {
        using TagLib.File f = TagLib.File.Create(path);
        var xiph = (TagLib.Ogg.XiphComment?)f.GetTag(TagLib.TagTypes.Xiph, false);
        return xiph?.GetFirstField("SOURCE");
    }

    [Fact]
    public void Writes_the_source_url_and_it_reads_back()
    {
        string path = Fixture();
        const string url = "https://open.qobuz.com/album/abc123";

        SourceTagWriter.TryWrite(path, url, Log);

        Assert.Equal(url, ReadSource(path));
    }

    // The audio must come through untouched — this runs on a finished download.
    // Hashing the frames, not STREAMINFO's stored MD5: Qobuz ships that field unset,
    // so comparing it would pass vacuously on exactly the files this guards.
    [Fact]
    public void Leaves_the_audio_stream_intact()
    {
        string path = Fixture();

        string beforeFrames = AudioFramesHash(path);
        string beforeSignature = StreamInfoMd5(path);

        SourceTagWriter.TryWrite(path, "https://tidal.com/album/1", Log);

        Assert.Equal(beforeFrames, AudioFramesHash(path));
        Assert.Equal(beforeSignature, StreamInfoMd5(path));
    }

    // Walks the metadata block chain to the one flagged last, then hashes the rest.
    private static string AudioFramesHash(string path)
    {
        using var fs = File.OpenRead(path);
        var magic = new byte[4];
        fs.ReadExactly(magic);
        Assert.Equal("fLaC", Encoding.ASCII.GetString(magic));

        var header = new byte[4];
        bool last;
        do
        {
            fs.ReadExactly(header);
            last = (header[0] & 0x80) != 0;
            fs.Position += (header[1] << 16) | (header[2] << 8) | header[3];
        }
        while (!last);

        Assert.True(fs.Position < fs.Length, "fixture has no audio frames");
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    // STREAMINFO is the first block, and its MD5 sits 18 bytes into the 34-byte body.
    private static string StreamInfoMd5(string path)
    {
        using var fs = File.OpenRead(path);
        var head = new byte[42];
        fs.ReadExactly(head);
        return Convert.ToHexString(head.AsSpan(26, 16));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Writes_nothing_when_there_is_no_url(string? url)
    {
        string path = Fixture();
        SourceTagWriter.TryWrite(path, url, Log);
        Assert.Null(ReadSource(path));
    }

    // Never fail a finished download over provenance metadata.
    [Fact]
    public void Does_not_throw_on_a_missing_file()
    {
        SourceTagWriter.TryWrite(Path.Combine(_dir, "nope.flac"), "https://example.com", Log);
    }
}
