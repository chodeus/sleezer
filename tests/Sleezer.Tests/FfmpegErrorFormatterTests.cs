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

    [Theory]
    [InlineData("Incorrect BOM value")]
    [InlineData("Incorrect BOM value: 0x1234")]
    [InlineData("Cannot read BOM value, input too short 1")]
    [InlineData("Error reading comment frame, skipped")]
    [InlineData("Error reading lyrics, skipped")]
    [InlineData("Error reading frame TXXX, skipped")]
    public void IsBenignMetadataNoise_flags_id3_tag_parse_lines(string line)
    {
        Assert.True(FfmpegErrorFormatter.IsBenignMetadataNoise(line));
    }

    [Theory]
    [InlineData("Header missing")]
    [InlineData("Invalid data found when processing input")]
    [InlineData("[mp3 @ 0x1] invalid new backstep -1")]
    [InlineData("Error while decoding stream #0:0: Invalid data found when processing input")]
    [InlineData("big_values too big")]
    public void IsBenignMetadataNoise_does_not_flag_real_decode_errors(string line)
    {
        Assert.False(FfmpegErrorFormatter.IsBenignMetadataNoise(line));
    }

    [Fact]
    public void StripBenignMetadataNoise_removes_id3_only_stderr_leaving_nothing()
    {
        // The exact stderr an old ffmpeg (4.x-6.x) emits for the Solomun/Sambada
        // bad-comment-tag file. Audio decodes clean, so this must NOT read as corrupt.
        string stderr = "Incorrect BOM value\nError reading comment frame, skipped";
        Assert.Equal(string.Empty, FfmpegErrorFormatter.StripBenignMetadataNoise(stderr));
    }

    [Fact]
    public void StripBenignMetadataNoise_preserves_real_decoder_errors()
    {
        string stderr = "[mp3 @ 0x1] invalid new backstep -1";
        string result = FfmpegErrorFormatter.StripBenignMetadataNoise(stderr);
        Assert.Contains("invalid new backstep -1", result);
    }

    [Fact]
    public void StripBenignMetadataNoise_keeps_real_error_mixed_with_benign_noise()
    {
        // A bad tag AND real corruption in the same file: the real error must survive
        // so the scanner still fails closed.
        string stderr = "Incorrect BOM value\nHeader missing\nError reading comment frame, skipped";
        string result = FfmpegErrorFormatter.StripBenignMetadataNoise(stderr);
        Assert.Equal("Header missing", result);
    }

    [Fact]
    public void StripBenignMetadataNoise_returns_empty_for_blank_input()
    {
        Assert.Equal(string.Empty, FfmpegErrorFormatter.StripBenignMetadataNoise(""));
        Assert.Equal(string.Empty, FfmpegErrorFormatter.StripBenignMetadataNoise("   \n"));
    }
}
