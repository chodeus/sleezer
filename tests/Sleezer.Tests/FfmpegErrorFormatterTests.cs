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
    public void CleanFfmpegErrors_deduplicates_repeated_lines()
    {
        string stderr = "[mp3 @ 0x1] Same error\n[mp3 @ 0x2] Same error\n[mp3 @ 0x3] Same error\n";
        Assert.Equal("Same error", FfmpegErrorFormatter.CleanFfmpegErrors(stderr));
    }

    [Fact]
    public void CleanFfmpegErrors_keeps_a_pipe_inside_an_error_line()
    {
        string stderr = "[in#0 @ 0x1] Error opening input file /music/Artist|Name - Track.flac.\n";
        Assert.Equal("Error opening input file /music/Artist|Name - Track.flac.", FfmpegErrorFormatter.CleanFfmpegErrors(stderr));
    }

    [Fact]
    public void CleanFfmpegErrors_joins_newline_separated_distinct_errors_with_pipes()
    {
        string stderr = "[a @ 0x1] First\n[b @ 0x2] Second";
        Assert.Equal("First | Second", FfmpegErrorFormatter.CleanFfmpegErrors(stderr));
    }

    // Captured from ffmpeg n9.0.2 (Lidarr's /config/ffmpeg) on files with 3 KB of random bytes mid-stream.
    internal const string DamagedFlacStderr =
        "[flac @ 0x1460e3582c40] invalid subframe padding\n" +
        "[flac @ 0x1460e3582c40] decode_frame() failed\n" +
        "[aist#0:0/flac @ 0x1460e357fe40] [dec:flac @ 0x1460e3589500] Decoding error: Invalid data found when processing input\n" +
        "[aist#0:0/flac @ 0x1460e357fe40] [dec:flac @ 0x1460e3589500] Error processing packet in decoder: Invalid data found when processing input\n" +
        "[aist#0:0/flac @ 0x1460e357fe40] [dec:flac @ 0x1460e3589500] Task finished with error code: -1094995529 (Invalid data found when processing input)\n" +
        "[aist#0:0/flac @ 0x1460e357fe40] [dec:flac @ 0x1460e3589500] Terminating thread with return code -1094995529 (Invalid data found when processing input)\n";

    private const string DamagedMp3Stderr =
        "[mp3float @ 0x152c41c64440] Header missing\n" +
        "[aist#0:0/mp3 @ 0x152c41c41840] [dec:mp3float @ 0x152c41c66500] Error submitting packet to decoder: Invalid data found when processing input\n" +
        "[aist#0:0/mp3 @ 0x152c41c41840] [dec:mp3float @ 0x152c41c66500] Error processing packet in decoder: Invalid data found when processing input\n" +
        "[aist#0:0/mp3 @ 0x152c41c41840] [dec:mp3float @ 0x152c41c66500] Task finished with error code: -1094995529 (Invalid data found when processing input)\n" +
        "[aist#0:0/mp3 @ 0x152c41c41840] [dec:mp3float @ 0x152c41c66500] Terminating thread with return code -1094995529 (Invalid data found when processing input)\n";

    [Fact]
    public void CleanFfmpegErrors_keeps_only_the_decoder_error_from_a_damaged_flac()
    {
        Assert.Equal("invalid subframe padding", FfmpegErrorFormatter.CleanFfmpegErrors(DamagedFlacStderr));
    }

    [Fact]
    public void CleanFfmpegErrors_keeps_only_the_decoder_error_from_a_damaged_mp3()
    {
        Assert.Equal("Header missing", FfmpegErrorFormatter.CleanFfmpegErrors(DamagedMp3Stderr));
    }

    [Fact]
    public void CleanFfmpegErrors_keeps_each_distinct_decoder_error_once_in_order()
    {
        // Several bad frames before -xerror stopped, as logged for real FLACs, plus the muxer's dts complaint.
        string stderr =
            "[flac @ 0x1] invalid residual\n" +
            "[flac @ 0x1] decode_frame() failed\n" +
            "[flac @ 0x1] invalid sync code\n" +
            "[flac @ 0x1] invalid frame header\n" +
            "[flac @ 0x1] decode_frame() failed\n" +
            "[flac @ 0x1] invalid sync code\n" +
            "[flac @ 0x1] invalid frame header\n" +
            "[aist#0:0/flac @ 0x2] [dec:flac @ 0x3] Decoding error: Invalid data found when processing input\n" +
            "[out#0/null @ 0x4] Application provided invalid, non monotonically increasing dts to muxer in stream 0: 5967360 >= 3720816\n" +
            "[aist#0:0/flac @ 0x2] [dec:flac @ 0x3] Task finished with error code: -1094995529 (Invalid data found when processing input)\n";

        Assert.Equal("invalid residual | invalid sync code | invalid frame header", FfmpegErrorFormatter.CleanFfmpegErrors(stderr));
    }

    [Fact]
    public void CleanFfmpegErrors_falls_back_to_the_first_line_when_ffmpeg_printed_only_follow_on_lines()
    {
        string stderr =
            "[aist#0:0/mp3 @ 0x1] [dec:mp3float @ 0x2] Error submitting packet to decoder: Invalid data found when processing input\n" +
            "[aist#0:0/mp3 @ 0x1] [dec:mp3float @ 0x2] Error processing packet in decoder: Invalid data found when processing input\n" +
            "[aist#0:0/mp3 @ 0x1] [dec:mp3float @ 0x2] Terminating thread with return code -1094995529 (Invalid data found when processing input)\n";

        Assert.Equal("Error submitting packet to decoder: Invalid data found when processing input", FfmpegErrorFormatter.CleanFfmpegErrors(stderr));
    }

    [Fact]
    public void CleanFfmpegErrors_drops_old_ffmpegs_error_while_decoding_line()
    {
        // ffmpeg 4.x-6.x prints this unprefixed line after the decoder's own error.
        string stderr =
            "[mp3float @ 0x1] Header missing\n" +
            "Error while decoding stream #0:0: Invalid data found when processing input\n";

        Assert.Equal("Header missing", FfmpegErrorFormatter.CleanFfmpegErrors(stderr));
    }

    [Fact]
    public void CleanFfmpegErrors_falls_back_past_the_muxers_dts_line()
    {
        string stderr =
            "[out#0/null @ 0x4] Application provided invalid, non monotonically increasing dts to muxer in stream 0: 5967360 >= 3720816\n" +
            "[aist#0:0/flac @ 0x2] [dec:flac @ 0x3] Decoding error: Invalid data found when processing input\n";

        Assert.Equal("Decoding error: Invalid data found when processing input", FfmpegErrorFormatter.CleanFfmpegErrors(stderr));
    }

    [Fact]
    public void CleanFfmpegErrors_keeps_a_decoder_error_that_quotes_ffmpegs_generic_text()
    {
        string stderr =
            "[flac @ 0x1] invalid sync code\n" +
            "[mjpeg @ 0x2] unable to decode APP fields: Invalid data found when processing input\n";

        Assert.Equal(
            "invalid sync code | unable to decode APP fields: Invalid data found when processing input",
            FfmpegErrorFormatter.CleanFfmpegErrors(stderr));
    }

    [Fact]
    public void DecodeFailureReason_puts_plain_words_before_the_decoder_error()
    {
        Assert.Equal("Audio won't decode (invalid subframe padding)", FfmpegErrorFormatter.DecodeFailureReason(DamagedFlacStderr, 1));
    }

    [Fact]
    public void DecodeFailureReason_leaves_tag_noise_out_of_a_real_decode_error()
    {
        string stderr = "Incorrect BOM value\n[mp3float @ 0x1] Header missing\nError reading comment frame, skipped\n";

        Assert.Equal("Audio won't decode (Header missing)", FfmpegErrorFormatter.DecodeFailureReason(stderr, 1));
    }

    [Fact]
    public void DecodeFailureReason_names_a_tag_or_cover_art_stop_when_that_is_all_ffmpeg_printed()
    {
        string stderr = "[flac @ 0x1] Could not read mimetype from an attached picture\n";

        Assert.Equal(
            "ffmpeg stopped at a damaged tag or cover art (Could not read mimetype from an attached picture)",
            FfmpegErrorFormatter.DecodeFailureReason(stderr, 1));
    }

    [Fact]
    public void DecodeFailureReason_gives_the_exit_code_when_ffmpeg_printed_nothing()
    {
        Assert.Equal("ffmpeg failed with exit code 69", FfmpegErrorFormatter.DecodeFailureReason("", 69));
    }

    [Fact]
    public void LogExcerpt_keeps_the_first_20_lines_and_counts_the_rest()
    {
        string stderr = string.Concat(Enumerable.Range(1, 25).Select(i => $"[flac @ 0x1] line {i}\n"));

        string excerpt = FfmpegErrorFormatter.LogExcerpt(stderr);

        Assert.StartsWith("[flac @ 0x1] line 1\n[flac @ 0x1] line 2\n", excerpt);
        Assert.EndsWith("[flac @ 0x1] line 20\n(5 more lines)", excerpt);
        Assert.DoesNotContain("line 21", excerpt);
    }

    [Fact]
    public void LogExcerpt_keeps_short_stderr_whole()
    {
        Assert.Equal("[flac @ 0x1] a\n[flac @ 0x1] b", FfmpegErrorFormatter.LogExcerpt("[flac @ 0x1] a\n[flac @ 0x1] b\n"));
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
