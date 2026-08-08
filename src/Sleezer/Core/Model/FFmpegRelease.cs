using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NzbDrone.Plugin.Sleezer.Core.Model
{
    /// <summary>
    /// Pure, dependency-free helpers for consuming the <c>chodeus/ffmpeg-static</c>
    /// release feed: platform→asset mapping, release-tag/version parsing, the 24h
    /// update-throttle decision, and SHA-256 verification. Deliberately free of
    /// Xabe / Lidarr / NLog dependencies so the test project can compile it directly
    /// (it pulls source files in rather than referencing the plugin assembly).
    /// </summary>
    internal static class FFmpegRelease
    {
        /// <summary>The single source of truth both sleezer and beatscheck consume.</summary>
        public const string Repo = "chodeus/ffmpeg-static";

        /// <summary>
        /// The list, not <c>/releases/latest</c>: the newest release does not always
        /// carry every platform, so each falls back to the newest one that has its assets.
        /// </summary>
        public const string ReleasesApiUrl = "https://api.github.com/repos/chodeus/ffmpeg-static/releases?per_page=100";

        /// <summary>How often the runtime re-checks the feed for a newer ffmpeg.</summary>
        public static readonly TimeSpan UpdateInterval = TimeSpan.FromHours(24);

        /// <summary>Name of the throttle timestamp file dropped in the ffmpeg directory.</summary>
        public const string CheckStampFileName = ".ffmpeg-update-check";

        /// <summary>The release asset names plus the local on-disk binary names for a platform.</summary>
        public sealed record PlatformAsset(string FfmpegAsset, string FfprobeAsset, string LocalFfmpegName, string LocalFfprobeName);

        /// <summary>
        /// Map an OS/arch to the <c>chodeus/ffmpeg-static</c> release asset names and the
        /// local binary names to write. Returns <c>null</c> where we publish no build
        /// (macOS, unsupported arch) — callers fall back to a system ffmpeg there.
        /// </summary>
        public static PlatformAsset? ForPlatform(OSPlatform os, Architecture arch)
        {
            if (os == OSPlatform.Linux)
            {
                return arch switch
                {
                    Architecture.X64 => new PlatformAsset("ffmpeg-linux64", "ffprobe-linux64", "ffmpeg", "ffprobe"),
                    Architecture.Arm64 => new PlatformAsset("ffmpeg-linuxarm64", "ffprobe-linuxarm64", "ffmpeg", "ffprobe"),
                    _ => null,
                };
            }

            if (os == OSPlatform.Windows)
            {
                return arch switch
                {
                    Architecture.X64 => new PlatformAsset("ffmpeg-win64.exe", "ffprobe-win64.exe", "ffmpeg.exe", "ffprobe.exe"),
                    Architecture.Arm64 => new PlatformAsset("ffmpeg-winarm64.exe", "ffprobe-winarm64.exe", "ffmpeg.exe", "ffprobe.exe"),
                    _ => null,
                };
            }

            // macOS and anything else: no published build — fall back to system ffmpeg.
            return null;
        }

        /// <summary>Resolve the asset for the running OS/architecture.</summary>
        public static PlatformAsset? ForCurrentPlatform()
        {
            OSPlatform os =
                RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? OSPlatform.Linux :
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? OSPlatform.Windows :
                OSPlatform.OSX;
            return ForPlatform(os, RuntimeInformation.OSArchitecture);
        }

        /// <summary>
        /// Parse a release tag like <c>n8.1.1</c> (or <c>8.1.1</c>, or <c>n8.1.1-extra</c>)
        /// into a <see cref="Version"/>. Returns <c>null</c> on anything unparseable.
        /// </summary>
        public static Version? ParseTag(string? tag)
        {
            if (string.IsNullOrWhiteSpace(tag))
                return null;

            string s = tag.Trim();
            if (s.Length > 1 && (s[0] == 'n' || s[0] == 'N' || s[0] == 'v' || s[0] == 'V') && char.IsDigit(s[1]))
                s = s.Substring(1);

            // Keep only the leading dotted-numeric run, dropping any "-suffix".
            int end = 0;
            while (end < s.Length && (char.IsDigit(s[end]) || s[end] == '.'))
                end++;
            s = s.Substring(0, end);

            return Version.TryParse(s, out Version? v) ? v : null;
        }

        /// <summary>
        /// Parse the first line of <c>ffmpeg -version</c> output into a <see cref="Version"/>.
        /// Handles both the distro form (<c>ffmpeg version 6.1.1-3ubuntu5 Copyright ...</c>) and
        /// the <c>chodeus/ffmpeg-static</c> form (<c>ffmpeg version n8.1.1-... Copyright ...</c>),
        /// where the version carries the same leading <c>n</c>/<c>v</c> build-tag prefix that
        /// <see cref="ParseTag"/> strips. Returns <c>null</c> on anything unparseable.
        /// </summary>
        public static Version? ParseFfmpegVersionLine(string? firstLine)
        {
            if (string.IsNullOrWhiteSpace(firstLine))
                return null;

            Match m = Regex.Match(firstLine, @"ffmpeg\s+version\s+[nNvV]?(?<ver>\d+(?:\.\d+){1,3})", RegexOptions.IgnoreCase);
            if (!m.Success)
                return null;

            return Version.TryParse(m.Groups["ver"].Value, out Version? v) ? v : null;
        }

        /// <summary>True when the interval has elapsed since the last check (or it never ran).</summary>
        public static bool ShouldCheck(DateTimeOffset? lastCheckUtc, DateTimeOffset nowUtc, TimeSpan interval)
            => lastCheckUtc is null || (nowUtc - lastCheckUtc.Value) >= interval;

        /// <summary>
        /// Pull the expected lowercase hex SHA-256 for <paramref name="fileName"/> out of a
        /// GNU-format SHA256SUMS body (<c>&lt;hash&gt;  &lt;name&gt;</c>). <c>null</c> if absent.
        /// </summary>
        public static string? ExpectedSha(string sha256SumsContent, string fileName)
        {
            if (string.IsNullOrEmpty(sha256SumsContent))
                return null;

            foreach (string raw in sha256SumsContent.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0)
                    continue;

                int sp = line.IndexOf(' ');
                if (sp <= 0)
                    continue;

                string hash = line.Substring(0, sp);
                // GNU sha256sum separates with two spaces (text) or " *" (binary).
                string name = line.Substring(sp).TrimStart(' ', '*');
                if (string.Equals(name, fileName, StringComparison.Ordinal))
                    return hash.ToLowerInvariant();
            }

            return null;
        }

        /// <summary>Verify raw bytes against an expected hex SHA-256 (case-insensitive).</summary>
        public static bool VerifySha(byte[] data, string? expectedHex)
        {
            if (string.IsNullOrWhiteSpace(expectedHex))
                return false;

            byte[] hash = SHA256.HashData(data);
            string actual = Convert.ToHexString(hash).ToLowerInvariant();
            return string.Equals(actual, expectedHex.Trim().ToLowerInvariant(), StringComparison.Ordinal);
        }

        /// <summary>The release tag plus an asset-name→download-URL map.</summary>
        public sealed record LatestRelease(string Tag, IReadOnlyDictionary<string, string> AssetUrls);

        /// <summary>
        /// Parse the GitHub "latest release" JSON into the tag plus an asset-name→URL map.
        /// Returns <c>null</c> if the payload has no string <c>tag_name</c>.
        /// </summary>
        public static LatestRelease? ParseLatestRelease(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("tag_name", out JsonElement tagEl)
                || tagEl.ValueKind != JsonValueKind.String)
                return null;

            string tag = tagEl.GetString()!;
            Dictionary<string, string> assets = new(StringComparer.Ordinal);
            if (root.TryGetProperty("assets", out JsonElement assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement a in assetsEl.EnumerateArray())
                {
                    if (a.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String
                        && a.TryGetProperty("browser_download_url", out JsonElement u) && u.ValueKind == JsonValueKind.String)
                    {
                        assets[n.GetString()!] = u.GetString()!;
                    }
                }
            }

            return new LatestRelease(tag, assets);
        }

        /// <summary>
        /// Parse a GitHub "list releases" payload, dropping drafts and prereleases —
        /// <c>/releases/latest</c> excluded those implicitly and this replaces it.
        /// Individual unparseable entries are skipped, but a blank or non-array body
        /// throws: that is a failed request (an error object, a proxy page), and
        /// returning it as "no releases" would look identical to a genuinely empty
        /// feed and silently stand in for a real answer.
        /// </summary>
        public static IReadOnlyList<LatestRelease> ParseReleases(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new InvalidOperationException($"Empty response from the {Repo} release feed.");

            List<LatestRelease> releases = new();
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException($"Expected a JSON array from the {Repo} release feed, got {doc.RootElement.ValueKind}.");

            foreach (JsonElement el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object)
                    continue;
                if (el.TryGetProperty("draft", out JsonElement d) && d.ValueKind == JsonValueKind.True)
                    continue;
                if (el.TryGetProperty("prerelease", out JsonElement p) && p.ValueKind == JsonValueKind.True)
                    continue;

                LatestRelease? rel = ParseLatestRelease(el.GetRawText());
                if (rel is not null)
                    releases.Add(rel);
            }

            return releases;
        }

        /// <summary>
        /// The newest release carrying every asset needed to install
        /// <paramref name="asset"/>: both binaries plus <c>SHA256SUMS</c>, since the
        /// download is verified against it. Ordered by parsed tag rather than by list
        /// position so it does not depend on GitHub's ordering; releases with an
        /// unparseable tag are skipped. <c>null</c> when no release qualifies.
        /// </summary>
        public static LatestRelease? SelectForAsset(IEnumerable<LatestRelease>? releases, PlatformAsset? asset)
        {
            if (releases is null || asset is null)
                return null;

            LatestRelease? best = null;
            Version? bestVersion = null;

            foreach (LatestRelease r in releases)
            {
                Version? v = ParseTag(r.Tag);
                if (v is null)
                    continue;
                if (!r.AssetUrls.ContainsKey(asset.FfmpegAsset)
                    || !r.AssetUrls.ContainsKey(asset.FfprobeAsset)
                    || !r.AssetUrls.ContainsKey("SHA256SUMS"))
                    continue;

                if (bestVersion is null || v > bestVersion)
                {
                    best = r;
                    bestVersion = v;
                }
            }

            return best;
        }
    }
}
