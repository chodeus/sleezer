using System;
using System.Collections.Generic;
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

    // A new FFmpeg major ships Linux-first, because the Windows mirror trails
    // upstream by weeks. n9.0 below is exactly that shape: newest, Linux-only.
    private const string ReleaseFeed = """
    [
      {
        "tag_name": "n9.0", "draft": false, "prerelease": false,
        "assets": [
          { "name": "ffmpeg-linux64",  "browser_download_url": "https://example/9/ffmpeg-linux64" },
          { "name": "ffprobe-linux64", "browser_download_url": "https://example/9/ffprobe-linux64" },
          { "name": "SHA256SUMS",      "browser_download_url": "https://example/9/SHA256SUMS" }
        ]
      },
      {
        "tag_name": "n8.1.2", "draft": false, "prerelease": false,
        "assets": [
          { "name": "ffmpeg-linux64",     "browser_download_url": "https://example/8/ffmpeg-linux64" },
          { "name": "ffprobe-linux64",    "browser_download_url": "https://example/8/ffprobe-linux64" },
          { "name": "ffmpeg-win64.exe",   "browser_download_url": "https://example/8/ffmpeg-win64.exe" },
          { "name": "ffprobe-win64.exe",  "browser_download_url": "https://example/8/ffprobe-win64.exe" },
          { "name": "SHA256SUMS",         "browser_download_url": "https://example/8/SHA256SUMS" }
        ]
      }
    ]
    """;

    [Fact]
    public void SelectForAsset_linux_takes_the_newest_release()
    {
        FFmpegRelease.LatestRelease? rel = FFmpegRelease.SelectForAsset(
            FFmpegRelease.ParseReleases(ReleaseFeed),
            FFmpegRelease.ForPlatform(OSPlatform.Linux, Architecture.X64));

        Assert.Equal("n9.0", rel!.Tag);
    }

    [Fact]
    public void SelectForAsset_windows_falls_back_past_a_linux_only_release()
    {
        FFmpegRelease.LatestRelease? rel = FFmpegRelease.SelectForAsset(
            FFmpegRelease.ParseReleases(ReleaseFeed),
            FFmpegRelease.ForPlatform(OSPlatform.Windows, Architecture.X64));

        Assert.Equal("n8.1.2", rel!.Tag);
        Assert.Equal("https://example/8/ffmpeg-win64.exe", rel.AssetUrls["ffmpeg-win64.exe"]);
    }

    [Fact]
    public void SelectForAsset_null_when_no_release_has_the_asset()
    {
        // winarm64 appears in neither release.
        Assert.Null(FFmpegRelease.SelectForAsset(
            FFmpegRelease.ParseReleases(ReleaseFeed),
            FFmpegRelease.ForPlatform(OSPlatform.Windows, Architecture.Arm64)));
    }

    [Fact]
    public void SelectForAsset_requires_SHA256SUMS()
    {
        // Both binaries present but no manifest — the download can't be verified.
        string json = """
        [{ "tag_name": "n9.0", "draft": false, "prerelease": false,
           "assets": [
             { "name": "ffmpeg-linux64",  "browser_download_url": "https://example/ffmpeg-linux64" },
             { "name": "ffprobe-linux64", "browser_download_url": "https://example/ffprobe-linux64" }
           ] }]
        """;
        Assert.Null(FFmpegRelease.SelectForAsset(
            FFmpegRelease.ParseReleases(json),
            FFmpegRelease.ForPlatform(OSPlatform.Linux, Architecture.X64)));
    }

    [Fact]
    public void SelectForAsset_requires_both_binaries()
    {
        // ffprobe missing — installing only half is worse than falling back.
        string json = """
        [{ "tag_name": "n9.0", "draft": false, "prerelease": false,
           "assets": [
             { "name": "ffmpeg-linux64", "browser_download_url": "https://example/ffmpeg-linux64" },
             { "name": "SHA256SUMS",     "browser_download_url": "https://example/SHA256SUMS" }
           ] }]
        """;
        Assert.Null(FFmpegRelease.SelectForAsset(
            FFmpegRelease.ParseReleases(json),
            FFmpegRelease.ForPlatform(OSPlatform.Linux, Architecture.X64)));
    }

    [Fact]
    public void ParseReleases_skips_drafts_and_prereleases()
    {
        string json = """
        [
          { "tag_name": "n9.9", "draft": true,  "prerelease": false, "assets": [] },
          { "tag_name": "n9.8", "draft": false, "prerelease": true,  "assets": [] },
          { "tag_name": "n8.1.2", "draft": false, "prerelease": false, "assets": [] }
        ]
        """;
        IReadOnlyList<FFmpegRelease.LatestRelease> rel = FFmpegRelease.ParseReleases(json);
        Assert.Single(rel);
        Assert.Equal("n8.1.2", rel[0].Tag);
    }

    [Fact]
    public void SelectForAsset_ignores_list_order()
    {
        // GitHub orders by creation date; selection must go by version.
        string json = """
        [
          { "tag_name": "n8.1.1", "draft": false, "prerelease": false,
            "assets": [ { "name": "ffmpeg-linux64", "browser_download_url": "https://example/a" },
                        { "name": "ffprobe-linux64", "browser_download_url": "https://example/b" },
                        { "name": "SHA256SUMS", "browser_download_url": "https://example/c" } ] },
          { "tag_name": "n9.0", "draft": false, "prerelease": false,
            "assets": [ { "name": "ffmpeg-linux64", "browser_download_url": "https://example/d" },
                        { "name": "ffprobe-linux64", "browser_download_url": "https://example/e" },
                        { "name": "SHA256SUMS", "browser_download_url": "https://example/f" } ] }
        ]
        """;
        FFmpegRelease.LatestRelease? rel = FFmpegRelease.SelectForAsset(
            FFmpegRelease.ParseReleases(json),
            FFmpegRelease.ForPlatform(OSPlatform.Linux, Architecture.X64));

        Assert.Equal("n9.0", rel!.Tag);
    }

    [Fact]
    public void ParseReleases_empty_on_non_array()
    {
        Assert.Empty(FFmpegRelease.ParseReleases("{ \"tag_name\": \"n9.0\" }"));
        Assert.Empty(FFmpegRelease.ParseReleases(""));
    }
}
