using NzbDrone.Plugin.Sleezer.Core.Tidal;
using Xunit;

namespace Sleezer.Tests;

// Covers upstream TrevTV/Lidarr.Plugin.Tidal issue #52: backslash and other
// Linux-permitted-but-confusing characters break Lidarr's path resolution.
public class TidalPathSanitizerTests
{
    [Theory]
    [InlineData("C:\\>FIXMBR", "C___FIXMBR")]
    [InlineData("foo\\bar", "foo_bar")]
    [InlineData("12:34", "12_34")]
    [InlineData("what?file", "what_file")]
    [InlineData("a*b", "a_b")]
    [InlineData("<weird>", "_weird_")]
    [InlineData("pipe|name", "pipe_name")]
    [InlineData("quote\"name", "quote_name")]
    [InlineData("slash/inside", "slash_inside")]
    public void CleanPath_replaces_filesystem_unsafe_characters(string input, string expected)
    {
        Assert.Equal(expected, TidalPathSanitizer.CleanPath(input));
    }

    [Theory]
    [InlineData("MASTER BOOT RECORD", "MASTER BOOT RECORD")]
    [InlineData("赤いスイートピー", "赤いスイートピー")]
    [InlineData("Niño Bonito", "Niño Bonito")]
    [InlineData("🎵 Track", "🎵 Track")]
    [InlineData("Solo dot.", "Solo dot")]
    [InlineData("", "")]
    public void CleanPath_passes_safe_unicode_through(string input, string expected)
    {
        Assert.Equal(expected, TidalPathSanitizer.CleanPath(input));
    }

    [Fact]
    public void CleanPath_handles_null()
    {
        Assert.Null(TidalPathSanitizer.CleanPath(null!));
    }

    [Fact]
    public void CleanPath_trims_trailing_dots_and_spaces()
    {
        Assert.Equal("Album", TidalPathSanitizer.CleanPath("Album..."));
        Assert.Equal("Album", TidalPathSanitizer.CleanPath("Album   "));
        Assert.Equal("Album", TidalPathSanitizer.CleanPath("Album . . "));
    }
}
