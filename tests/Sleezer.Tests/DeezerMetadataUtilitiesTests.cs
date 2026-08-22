using NzbDrone.Core.Download.Clients.Deezer;
using Xunit;

namespace Sleezer.Tests;

// Deezer used to sanitize with a bare Path.GetInvalidFileNameChars() loop, which on
// Linux covers only '/' and NUL — so ':' and friends reached the filesystem. These
// pin the delegation to the canonical sanitizer.
public class DeezerMetadataUtilitiesTests
{
    [Theory]
    [InlineData("Live at Wembley: The Return", "Live at Wembley_ The Return")]
    [InlineData("AC\\DC", "AC_DC")]
    [InlineData("What?", "What_")]
    [InlineData("A*B", "A_B")]
    [InlineData("<tag>", "_tag_")]
    [InlineData("pipe|name", "pipe_name")]
    [InlineData("quote\"name", "quote_name")]
    [InlineData("slash/inside", "slash_inside")]
    public void CleanPath_strips_characters_linux_would_otherwise_allow(string input, string expected)
    {
        Assert.Equal(expected, MetadataUtilities.CleanPath(input));
    }

    [Theory]
    [InlineData("Trailing dot.", "Trailing dot")]
    [InlineData("Trailing space ", "Trailing space")]
    public void CleanPath_trims_trailing_dots_and_spaces(string input, string expected)
    {
        Assert.Equal(expected, MetadataUtilities.CleanPath(input));
    }

    [Theory]
    [InlineData("Niño Bonito")]
    [InlineData("赤いスイートピー")]
    [InlineData("Perfectly Ordinary Album")]
    public void CleanPath_passes_safe_titles_through(string input)
    {
        Assert.Equal(input, MetadataUtilities.CleanPath(input));
    }
}
