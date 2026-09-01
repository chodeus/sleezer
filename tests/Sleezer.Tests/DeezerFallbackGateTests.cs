using NzbDrone.Plugin.Sleezer.Core.Deezer;
using Xunit;

namespace Sleezer.Tests;

public class DeezerFallbackGateTests
{
    private static FallbackCandidate Original(string? isrc = "AUUM71234567") =>
        new(isrc, "Angel", null, 213, Explicit: false);

    [Fact]
    public void Same_isrc_is_accepted_even_when_titles_differ()
    {
        var candidate = new FallbackCandidate("auum71234567", "Angel (Album Edit)", "Album Edit", 305, Explicit: true);

        Assert.True(DeezerFallbackGate.Accept(Original(), candidate, out var reason));
        Assert.Contains("ISRC", reason);
    }

    [Fact]
    public void Different_isrc_is_rejected_even_when_everything_else_matches()
    {
        // The clean-edit / re-record trap: identical title and duration, different recording.
        var candidate = new FallbackCandidate("AUUM79999999", "Angel", null, 213, Explicit: false);

        Assert.False(DeezerFallbackGate.Accept(Original(), candidate, out var reason));
        Assert.Contains("ISRC mismatch", reason);
    }

    [Fact]
    public void Missing_isrc_accepts_on_matching_title_version_duration_and_explicit()
    {
        var candidate = new FallbackCandidate(null, "  angel ", null, 215, Explicit: false);

        Assert.True(DeezerFallbackGate.Accept(Original(isrc: null), candidate, out _));
    }

    [Theory]
    [InlineData(216, true)]  // exactly at the 3s tolerance
    [InlineData(217, false)] // one past it
    [InlineData(209, false)]
    public void Missing_isrc_enforces_duration_tolerance(int candidateDuration, bool expected)
    {
        var candidate = new FallbackCandidate(null, "Angel", null, candidateDuration, Explicit: false);

        Assert.Equal(expected, DeezerFallbackGate.Accept(Original(isrc: null), candidate, out _));
    }

    [Fact]
    public void Missing_isrc_rejects_version_mismatch()
    {
        var candidate = new FallbackCandidate(null, "Angel", "Live", 213, Explicit: false);

        Assert.False(DeezerFallbackGate.Accept(Original(isrc: null), candidate, out var reason));
        Assert.Contains("version", reason);
    }

    [Fact]
    public void Missing_isrc_rejects_explicit_flag_mismatch()
    {
        var candidate = new FallbackCandidate(null, "Angel", null, 213, Explicit: true);

        Assert.False(DeezerFallbackGate.Accept(Original(isrc: null), candidate, out var reason));
        Assert.Contains("explicit", reason);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("Angel", null)]
    [InlineData(null, "Angel")]
    [InlineData("Angel", "   ")]
    public void Missing_isrc_rejects_missing_titles(string? originalTitle, string? candidateTitle)
    {
        var original = new FallbackCandidate(null, originalTitle, null, 213, Explicit: false);
        var candidate = new FallbackCandidate(null, candidateTitle, null, 213, Explicit: false);

        Assert.False(DeezerFallbackGate.Accept(original, candidate, out var reason));
        Assert.Contains("title", reason);
    }

    [Fact]
    public void Missing_isrc_rejects_title_mismatch()
    {
        var candidate = new FallbackCandidate(null, "Guardian Angel", null, 213, Explicit: false);

        Assert.False(DeezerFallbackGate.Accept(Original(isrc: null), candidate, out var reason));
        Assert.Contains("title", reason);
    }

    [Fact]
    public void One_sided_isrc_falls_back_to_the_title_gate()
    {
        var candidate = new FallbackCandidate("AUUM71234567", "Angel", null, 213, Explicit: false);

        Assert.True(DeezerFallbackGate.Accept(Original(isrc: null), candidate, out _));
    }
}
