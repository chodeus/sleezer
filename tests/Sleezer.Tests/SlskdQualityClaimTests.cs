using NzbDrone.Plugin.Sleezer.Indexers.Soulseek;
using Xunit;

namespace Sleezer.Tests;

// Issue #85: a slskd release was labelled [FLAC 24bit] purely because the sharing
// peer said so. Soulseek cannot expose a file header before transfer, so these are
// the two things that can still be checked about the claim itself.
public class SlskdQualityClaimTests
{
    private static SlskdFileData File(int? bitDepth, int? sampleRate = 44100, long size = 30_000_000, int length = 240)
        => new("track.flac", null, bitDepth, size, length, "flac", sampleRate, 1, false);

    [Fact]
    public void UnanimousOrNull_returns_the_value_when_every_file_agrees()
    {
        SlskdFileData[] files = [File(24), File(24), File(24)];
        Assert.Equal(24, SlskdItemsParser.UnanimousOrNull(files, f => f.BitDepth));
    }

    // The old majority rule advertised 24-bit for this folder and misdescribed the rest.
    [Fact]
    public void UnanimousOrNull_returns_null_when_files_disagree()
    {
        SlskdFileData[] files = [File(24), File(24), File(16)];
        Assert.Null(SlskdItemsParser.UnanimousOrNull(files, f => f.BitDepth));
    }

    [Fact]
    public void UnanimousOrNull_returns_null_when_any_file_is_missing_the_value()
    {
        SlskdFileData[] files = [File(24), File(null), File(24)];
        Assert.Null(SlskdItemsParser.UnanimousOrNull(files, f => f.BitDepth));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void UnanimousOrNull_treats_nonpositive_as_missing(int bogus)
    {
        SlskdFileData[] files = [File(bogus), File(bogus)];
        Assert.Null(SlskdItemsParser.UnanimousOrNull(files, f => f.BitDepth));
    }

    [Fact]
    public void UnanimousOrNull_returns_null_for_an_empty_set()
    {
        Assert.Null(SlskdItemsParser.UnanimousOrNull([], f => f.BitDepth));
    }

    // The issue's worked example: 24/96 advertised against an implied ~900 kbps.
    [Fact]
    public void DepthClaimIsPossible_rejects_a_hires_claim_the_bytes_cannot_support()
    {
        // 900 kbps over 240s ≈ 27 MB. Even mono 24/96 floors at ~1152 kbps.
        Assert.False(SlskdItemsParser.DepthClaimIsPossible(24, 96000, totalSize: 27_000_000, totalDurationSeconds: 240));
    }

    [Fact]
    public void DepthClaimIsPossible_accepts_a_genuine_hires_folder()
    {
        // ~3000 kbps over 240s ≈ 90 MB — comfortably above the 24/96 floor.
        Assert.True(SlskdItemsParser.DepthClaimIsPossible(24, 96000, totalSize: 90_000_000, totalDurationSeconds: 240));
    }

    // Explicitly documented as out of reach: the mono floor for 24/44.1 sits below
    // a normal 16/44.1 bitrate, so this pair cannot be separated before download.
    [Fact]
    public void DepthClaimIsPossible_cannot_separate_24_from_16_at_44_1()
    {
        Assert.True(SlskdItemsParser.DepthClaimIsPossible(24, 44100, totalSize: 30_000_000, totalDurationSeconds: 240));
    }

    [Theory]
    [InlineData(null, 30_000_000, 240)]  // no sample rate advertised
    [InlineData(96000, 0, 240)]          // no size
    [InlineData(96000, 30_000_000, 0)]   // no duration
    public void DepthClaimIsPossible_defers_when_there_is_nothing_to_disprove_it_with(int? sampleRate, long size, int duration)
    {
        Assert.True(SlskdItemsParser.DepthClaimIsPossible(24, sampleRate, size, duration));
    }
}

// The confinement guard in SlskdDownloadManager gates folder deletion. It is not
// reachable from the test project (it needs Lidarr.Core), so this pins the rule it
// implements: on a case-sensitive filesystem a sibling that differs only by case is
// outside the root, and must not be treated as inside it.
public class PathContainmentRuleTests
{
    private static bool IsStrictDescendant(string candidate, string root)
    {
        var fullRoot = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        var full = System.IO.Path.GetFullPath(candidate);
        var withSep = fullRoot + System.IO.Path.DirectorySeparatorChar;
        return full.Length > withSep.Length && full.StartsWith(withSep, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/downloads/music/artist/album", "/downloads/music", true)]
    [InlineData("/downloads/MUSIC/artist/album", "/downloads/music", false)]
    [InlineData("/downloads/music-old/artist", "/downloads/music", false)]
    [InlineData("/downloads/music", "/downloads/music", false)]
    [InlineData("/downloads", "/downloads/music", false)]
    public void Containment_requires_a_case_sensitive_separator_boundary(string candidate, string root, bool expected)
    {
        Assert.Equal(expected, IsStrictDescendant(candidate, root));
    }
}
