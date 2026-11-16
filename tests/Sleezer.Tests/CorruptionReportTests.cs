using NzbDrone.Plugin.Sleezer.Core.PostProcessing;
using Xunit;

namespace Sleezer.Tests;

public class CorruptionReportTests
{
    [Fact]
    public void FailureMessage_names_the_one_failed_file_and_its_reason()
    {
        string message = CorruptionReport.FailureMessage([new("03 - Track Three.flac", "Audio won't decode (invalid residual)")]);

        Assert.Equal("Sleezer: 1 file failed the corruption check — 03 - Track Three.flac: Audio won't decode (invalid residual)", message);
    }

    [Fact]
    public void FailureMessage_lists_every_failed_file_in_scan_order()
    {
        string message = CorruptionReport.FailureMessage(
        [
            new("03 - Track Three.flac", "Audio won't decode (invalid residual)"),
            new("07 - Track Seven.flac", "Audio won't decode (invalid sync code | invalid frame header)"),
            new("11 - Track Eleven.flac", "Decode timed out (>120s)"),
        ]);

        Assert.Equal(
            "Sleezer: 3 files failed the corruption check — " +
            "03 - Track Three.flac: Audio won't decode (invalid residual); " +
            "07 - Track Seven.flac: Audio won't decode (invalid sync code | invalid frame header); " +
            "11 - Track Eleven.flac: Decode timed out (>120s)",
            message);
    }

    [Fact]
    public void FailureMessage_says_so_when_a_strike_has_no_reason()
    {
        string message = CorruptionReport.FailureMessage([new("01 - Track One.mp3", null)]);

        Assert.Equal("Sleezer: 1 file failed the corruption check — 01 - Track One.mp3: no reason recorded", message);
    }

    [Fact]
    public void FailureMessage_carries_the_scanners_reason_for_real_ffmpeg_output()
    {
        string message = CorruptionReport.FailureMessage([new("03 - Track Three.flac", FfmpegErrorFormatter.DecodeFailureReason(FfmpegErrorFormatterTests.DamagedFlacStderr, 1))]);

        Assert.Equal("Sleezer: 1 file failed the corruption check — 03 - Track Three.flac: Audio won't decode (invalid subframe padding)", message);
    }

    [Fact]
    public void FailureMessage_stops_listing_at_1000_characters_and_counts_the_rest()
    {
        CorruptionStrike[] strikes = [.. Enumerable.Range(1, 30).Select(i => new CorruptionStrike($"{i:00} - Track Number {i:00}.flac", "Audio won't decode (invalid residual)"))];

        string message = CorruptionReport.FailureMessage(strikes);

        Assert.StartsWith("Sleezer: 30 files failed the corruption check — 01 - Track Number 01.flac: Audio won't decode (invalid residual); 02 - ", message);
        Assert.EndsWith("; 13 - Track Number 13.flac: Audio won't decode (invalid residual); +17 more (see the Lidarr log)", message);
        Assert.True(message.Length <= 1000, $"{message.Length} characters");
    }

    [Fact]
    public void FailureMessage_cuts_a_first_entry_too_long_to_fit()
    {
        string message = CorruptionReport.FailureMessage(
        [
            new("01 - Track One.flac", "Audio won't decode (" + new string('x', 1200) + ")"),
            new("02 - Track Two.flac", "Audio won't decode (invalid residual)"),
        ]);

        Assert.StartsWith("Sleezer: 2 files failed the corruption check — 01 - Track One.flac: Audio won't decode (xxx", message);
        Assert.EndsWith("xxx…; +1 more (see the Lidarr log)", message);
        Assert.Equal(1000, message.Length);
    }

    [Fact]
    public void FileLabel_keeps_the_disc_folder_of_a_multi_disc_download()
    {
        string folder = Path.Combine(Path.GetTempPath(), "Artist - Album");

        Assert.Equal(Path.Combine("CD2", "01 - Intro.flac"), CorruptionReport.FileLabel(folder, Path.Combine(folder, "CD2", "01 - Intro.flac")));
        Assert.Equal("03 - Track Three.flac", CorruptionReport.FileLabel(folder, Path.Combine(folder, "03 - Track Three.flac")));
    }
}
