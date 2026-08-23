using System.IO;
using NzbDrone.Core.Download.Clients.Tidal;
using Xunit;

namespace Sleezer.Tests;

// Tidal is the only client that shells out to ffmpeg during a download, for its
// FLAC-from-M4A extraction. If the configured directory stops reaching this wrapper the
// extraction silently falls back to PATH, which in the Lidarr image misses /usr/bin —
// a working feature degrading with no error. These pin the resolution itself, since
// exercising it end to end needs a live Tidal account.
public class FFmpegBinaryResolutionTests : System.IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sleezer-ffmpeg-" + Path.GetRandomFileName());

    public FFmpegBinaryResolutionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        FFMPEG.SetBinaryDirectory(null);
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
        System.GC.SuppressFinalize(this);
    }

    [Fact]
    public void Resolves_to_the_configured_directory_when_the_binary_is_there()
    {
        var binary = Path.Combine(_dir, "ffmpeg");
        File.WriteAllText(binary, string.Empty);

        FFMPEG.SetBinaryDirectory(_dir);

        Assert.Equal(binary, FFMPEG.ResolveBinary("ffmpeg"));
    }

    [Fact]
    public void Resolves_the_exe_variant_for_a_directory_holding_Windows_binaries()
    {
        var binary = Path.Combine(_dir, "ffprobe.exe");
        File.WriteAllText(binary, string.Empty);

        FFMPEG.SetBinaryDirectory(_dir);

        Assert.Equal(binary, FFMPEG.ResolveBinary("ffprobe"));
    }

    // The configured directory being wrong must not be fatal — PATH may still have it.
    [Fact]
    public void Falls_back_to_PATH_when_the_configured_directory_lacks_the_binary()
    {
        FFMPEG.SetBinaryDirectory(_dir);

        Assert.Equal("ffmpeg", FFMPEG.ResolveBinary("ffmpeg"));
    }

    [Fact]
    public void Falls_back_to_PATH_when_no_directory_is_configured()
    {
        FFMPEG.SetBinaryDirectory(null);

        Assert.Equal("ffmpeg", FFMPEG.ResolveBinary("ffmpeg"));
    }

    // Whitespace is what an empty Lidarr settings field yields; treating it as a real
    // directory would send every lookup at Path.Combine(" ", name).
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Treats_a_blank_configured_directory_as_unset(string configured)
    {
        FFMPEG.SetBinaryDirectory(configured);

        Assert.Equal("ffmpeg", FFMPEG.ResolveBinary("ffmpeg"));
    }
}
