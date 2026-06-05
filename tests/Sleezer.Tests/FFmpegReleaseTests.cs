using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using NzbDrone.Plugin.Sleezer.Core.Model;
using Xunit;

namespace Sleezer.Tests;

// Pure-helper coverage for the chodeus/ffmpeg-static consumer: platform→asset
// mapping, release-tag/version parsing, the 24h update throttle, and SHA-256
// verification. No network — the orchestration in FFmpegInstaller is not unit-tested.
public class FFmpegReleaseTests
{
    [Fact]
    public void ForPlatform_linux_x64_maps_to_static_binaries()
    {
        FFmpegRelease.PlatformAsset? a = FFmpegRelease.ForPlatform(OSPlatform.Linux, Architecture.X64);
        Assert.NotNull(a);
        Assert.Equal("ffmpeg-linux64", a!.FfmpegAsset);
        Assert.Equal("ffprobe-linux64", a.FfprobeAsset);
        Assert.Equal("ffmpeg", a.LocalFfmpegName);
        Assert.Equal("ffprobe", a.LocalFfprobeName);
    }

    [Fact]
    public void ForPlatform_linux_arm64_maps_to_arm_assets()
    {
        FFmpegRelease.PlatformAsset? a = FFmpegRelease.ForPlatform(OSPlatform.Linux, Architecture.Arm64);
        Assert.Equal("ffmpeg-linuxarm64", a!.FfmpegAsset);
        Assert.Equal("ffmpeg", a.LocalFfmpegName);
    }

    [Fact]
    public void ForPlatform_windows_x64_uses_exe_names()
    {
        FFmpegRelease.PlatformAsset? a = FFmpegRelease.ForPlatform(OSPlatform.Windows, Architecture.X64);
        Assert.Equal("ffmpeg-win64.exe", a!.FfmpegAsset);
        Assert.Equal("ffmpeg.exe", a.LocalFfmpegName);
        Assert.Equal("ffprobe.exe", a.LocalFfprobeName);
    }

    [Fact]
    public void ForPlatform_macos_has_no_build()
        => Assert.Null(FFmpegRelease.ForPlatform(OSPlatform.OSX, Architecture.X64));

    [Fact]
    public void ForPlatform_unsupported_arch_is_null()
        => Assert.Null(FFmpegRelease.ForPlatform(OSPlatform.Linux, Architecture.X86));

    [Theory]
    [InlineData("n8.1.1", "8.1.1")]
    [InlineData("8.1.1", "8.1.1")]
    [InlineData("n8.1", "8.1")]
    [InlineData("n8.1.1-extra", "8.1.1")]
    [InlineData("v8.1.1", "8.1.1")]
    public void ParseTag_parses_versions(string tag, string expected)
        => Assert.Equal(Version.Parse(expected), FFmpegRelease.ParseTag(tag));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nightly")]
    [InlineData("n")]
    public void ParseTag_returns_null_on_garbage(string? tag)
        => Assert.Null(FFmpegRelease.ParseTag(tag));

    [Theory]
    // chodeus/ffmpeg-static emits an n-prefixed build tag — the regression this guards.
    [InlineData("ffmpeg version n8.1.1-... Copyright (c) 2000-2026 the FFmpeg developers", "8.1.1")]
    [InlineData("ffmpeg version n8.1.1 Copyright (c) 2000-2026 the FFmpeg developers", "8.1.1")]
    // Distro / other common forms still parse.
    [InlineData("ffmpeg version 6.1.1-3ubuntu5 Copyright (c) 2000-2023 the FFmpeg developers", "6.1.1")]
    [InlineData("ffmpeg version 8.1.1 Copyright", "8.1.1")]
    [InlineData("ffmpeg version v8.1 Copyright", "8.1")]
    public void ParseFfmpegVersionLine_parses_version(string firstLine, string expected)
        => Assert.Equal(Version.Parse(expected), FFmpegRelease.ParseFfmpegVersionLine(firstLine));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ffmpeg version N-118491-gabc1234")] // git nightly, no dotted-numeric
    [InlineData("not an ffmpeg version line")]
    public void ParseFfmpegVersionLine_returns_null_on_unparseable(string? firstLine)
        => Assert.Null(FFmpegRelease.ParseFfmpegVersionLine(firstLine));

    [Fact]
    public void ShouldCheck_true_when_never_checked()
        => Assert.True(FFmpegRelease.ShouldCheck(null, DateTimeOffset.UtcNow, TimeSpan.FromHours(24)));

    [Fact]
    public void ShouldCheck_false_within_interval()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.False(FFmpegRelease.ShouldCheck(now.AddHours(-1), now, TimeSpan.FromHours(24)));
    }

    [Fact]
    public void ShouldCheck_true_after_interval()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.True(FFmpegRelease.ShouldCheck(now.AddHours(-25), now, TimeSpan.FromHours(24)));
    }

    [Fact]
    public void ExpectedSha_extracts_matching_line()
    {
        string sums = "aaaa  ffmpeg-linux64\nbbbb  ffprobe-linux64\n";
        Assert.Equal("aaaa", FFmpegRelease.ExpectedSha(sums, "ffmpeg-linux64"));
        Assert.Equal("bbbb", FFmpegRelease.ExpectedSha(sums, "ffprobe-linux64"));
        Assert.Null(FFmpegRelease.ExpectedSha(sums, "missing"));
    }

    [Fact]
    public void VerifySha_accepts_correct_and_rejects_tampered()
    {
        byte[] data = Encoding.UTF8.GetBytes("ffmpeg bytes");
        string good = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        Assert.True(FFmpegRelease.VerifySha(data, good));
        Assert.True(FFmpegRelease.VerifySha(data, good.ToUpperInvariant())); // case-insensitive
        Assert.False(FFmpegRelease.VerifySha(data, new string('0', 64)));
        Assert.False(FFmpegRelease.VerifySha(data, null));
    }

    [Fact]
    public void ExpectedSha_then_VerifySha_round_trip()
    {
        byte[] data = Encoding.UTF8.GetBytes("payload");
        string hex = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        string sums = $"{hex}  ffmpeg-linux64\n";
        Assert.True(FFmpegRelease.VerifySha(data, FFmpegRelease.ExpectedSha(sums, "ffmpeg-linux64")));
    }

    [Fact]
    public void ParseLatestRelease_extracts_tag_and_assets()
    {
        string json = """
        {
          "tag_name": "n8.1.1",
          "assets": [
            { "name": "ffmpeg-linux64", "browser_download_url": "https://example/ffmpeg-linux64" },
            { "name": "SHA256SUMS", "browser_download_url": "https://example/SHA256SUMS" }
          ]
        }
        """;
        FFmpegRelease.LatestRelease? rel = FFmpegRelease.ParseLatestRelease(json);
        Assert.NotNull(rel);
        Assert.Equal("n8.1.1", rel!.Tag);
        Assert.Equal("https://example/ffmpeg-linux64", rel.AssetUrls["ffmpeg-linux64"]);
        Assert.True(rel.AssetUrls.ContainsKey("SHA256SUMS"));
    }

    [Fact]
    public void ParseLatestRelease_null_without_tag()
    {
        Assert.Null(FFmpegRelease.ParseLatestRelease("{ \"assets\": [] }"));
        Assert.Null(FFmpegRelease.ParseLatestRelease(""));
    }
}
