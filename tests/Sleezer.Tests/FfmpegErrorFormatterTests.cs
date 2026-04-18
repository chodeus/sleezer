using NzbDrone.Plugin.Sleezer.Core.PostProcessing;
using Xunit;

namespace Sleezer.Tests;

public class FfmpegErrorFormatterTests
{
    [Fact]
    public void CleanFfmpegErrors_returns_default_for_empty_stderr()
    {
        Assert.Equal("Non-zero exit code", FfmpegErrorFormatter.CleanFfmpegErrors(""));
        Assert.Equal("Non-zero exit code", FfmpegErrorFormatter.CleanFfmpegErrors("   \n"));
    }

    [Fact]
    public void CleanFfmpegErrors_strips_codec_address_prefixes()
    {
        string stderr = "[mp3 @ 0x7f8a4c00] Header missing\n[mp3 @ 0x7f8a4c00] invalid frame";
        string result = FfmpegErrorFormatter.CleanFfmpegErrors(stderr);
        Assert.DoesNotContain("@", result);
        Assert.DoesNotContain("0x", result);
        Assert.Contains("Header missing", result);
        Assert.Contains("invalid frame", result);
    }

    [Fact]
    public void CleanFfmpegErrors_deduplicates_pipe_separated_repeats()
    {
        // The formatter splits on " | " and dedups — mirrors ffmpeg's own repeat-error format
        // "X | X | X" that shows up when a single frame fails and -xerror hasn't fired yet.
        string stderr = "[mp3 @ 0x1] Same error | [mp3 @ 0x2] Same error | [mp3 @ 0x3] Same error";
        string result = FfmpegErrorFormatter.CleanFfmpegErrors(stderr);
        int occurrences = result.Split("Same error").Length - 1;
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void CleanFfmpegErrors_preserves_newline_separated_distinct_errors()
    {
        // Newline-separated stderr collapses to one whitespace-joined string — documents
        // the current behavior so future refactors don't silently change it.
        string stderr = "[a @ 0x1] First\n[b @ 0x2] Second";
        string result = FfmpegErrorFormatter.CleanFfmpegErrors(stderr);
        Assert.Contains("First", result);
        Assert.Contains("Second", result);
    }

    [Fact]
    public void CleanFfmpegErrors_collapses_whitespace()
    {
        string stderr = "[x @ 0x1]    Error   with    gaps";
        string result = FfmpegErrorFormatter.CleanFfmpegErrors(stderr);
        Assert.Equal("Error with gaps", result);
    }

    [Fact]
    public void CleanFfmpegErrors_returns_default_when_only_prefixes_present()
    {
        // Hypothetical stderr that's all prefix and no content — shouldn't crash, should fall back.
        string stderr = "[mp3 @ 0x1]  \n[aac @ 0x2]  ";
        string result = FfmpegErrorFormatter.CleanFfmpegErrors(stderr);
        Assert.Equal("Non-zero exit code", result);
    }
}
