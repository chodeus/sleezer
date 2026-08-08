using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Instrumentation;

namespace NzbDrone.Plugin.Sleezer.Core.Model
{
    /// <summary>
    /// Acquires the ffmpeg/ffprobe binaries from the <c>chodeus/ffmpeg-static</c>
    /// release feed at runtime — replacing the old <c>Xabe.FFmpeg.Downloader</c>
    /// path (which was frozen at ffmpeg 4.4.1 on ffbinaries.com). Downloads are
    /// SHA-256 verified against the release <c>SHA256SUMS</c> before they are moved
    /// into place. <see cref="EnsureUpToDateAsync"/> adds a throttled, best-effort
    /// auto-update so the plugin tracks the latest published ffmpeg over time.
    /// </summary>
    internal static class FFmpegInstaller
    {
        private static readonly HttpClient _http = CreateClient();

        // Serialise all downloads so concurrent post-process passes (Deezer, Tidal,
        // Slskd) and the settings "Test" button can't race on the same directory.
        private static readonly SemaphoreSlim _gate = new(1, 1);

        // Worst-case bound for the auto-update path, which callers invoke with no
        // deadline of their own. The install path is bounded by its caller instead.
        private static readonly TimeSpan UpdateDeadline = TimeSpan.FromMinutes(5);

        private static HttpClient CreateClient()
        {
            // Bounded per request by the caller's CancellationToken, not by a global timeout.
            HttpClient client = new() { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Sleezer-FFmpegUpdater");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        /// <summary>
        /// Download the latest published ffmpeg/ffprobe for this platform into
        /// <paramref name="targetDir"/>, verifying each against the release
        /// <c>SHA256SUMS</c>. No-op on platforms we don't publish (macOS) — callers
        /// fall back to a system ffmpeg there. Bounded by <paramref name="ct"/>.
        /// </summary>
        internal static async Task DownloadLatestAsync(string targetDir, CancellationToken ct)
        {
            FFmpegRelease.PlatformAsset? asset = FFmpegRelease.ForCurrentPlatform();
            Logger logger = NzbDroneLogger.GetLogger(typeof(FFmpegInstaller));
            if (asset is null)
            {
                logger.Debug("No {Repo} build for {OS}/{Arch}; relying on a system ffmpeg.", FFmpegRelease.Repo, RuntimeInformation.OSDescription, RuntimeInformation.OSArchitecture);
                return;
            }

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                FFmpegRelease.LatestRelease release = await ResolveReleaseAsync(asset, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"No {FFmpegRelease.Repo} release publishes {asset.FfmpegAsset}.");
                await DownloadBothAsync(release, asset, targetDir, logger, ct).ConfigureAwait(false);
                logger.Info("Installed ffmpeg {Tag} ({Asset}) to {Dir} from {Repo}.", release.Tag, asset.FfmpegAsset, targetDir, FFmpegRelease.Repo);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Best-effort: at most once per <see cref="FFmpegRelease.UpdateInterval"/>, check the
        /// feed and download a newer ffmpeg into <paramref name="targetDir"/> when one exists.
        /// Never throws and never blocks beyond <see cref="UpdateDeadline"/> — a failure leaves
        /// the current binary in place. Intended to be invoked fire-and-forget from the
        /// post-process resolve path.
        /// </summary>
        internal static async Task EnsureUpToDateAsync(string targetDir, Logger logger, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(targetDir))
                return;

            FFmpegRelease.PlatformAsset? asset = FFmpegRelease.ForCurrentPlatform();
            if (asset is null)
                return;

            // Throttle gate — cheap mtime read, no network when recently checked.
            string stamp = Path.Combine(targetDir, FFmpegRelease.CheckStampFileName);
            if (!FFmpegRelease.ShouldCheck(GetStampUtc(stamp), DateTimeOffset.UtcNow, FFmpegRelease.UpdateInterval))
                return;

            // Only one updater at a time; if another holds the gate, let it handle it.
            if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
                return;

            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(UpdateDeadline);
            try
            {
                // Re-read under the lock in case another pass just refreshed it.
                if (!FFmpegRelease.ShouldCheck(GetStampUtc(stamp), DateTimeOffset.UtcNow, FFmpegRelease.UpdateInterval))
                    return;

                // Stamp up front so a transient failure doesn't re-hammer GitHub for 24h.
                TouchStamp(stamp);

                FFmpegRelease.LatestRelease? release = await ResolveReleaseAsync(asset, deadline.Token).ConfigureAwait(false);
                Version? latest = FFmpegRelease.ParseTag(release?.Tag);
                if (release is null || latest is null)
                {
                    logger.Debug("FFmpeg update check: no {Repo} release publishes {Asset}; keeping current binary.", FFmpegRelease.Repo, asset.FfmpegAsset);
                    return;
                }

                Version? installed = AudioMetadataHandler.ProbeFfmpegVersion(Path.Combine(targetDir, asset.LocalFfmpegName));
                if (installed is not null && installed >= latest)
                {
                    logger.Trace("FFmpeg up to date: {Installed} >= latest {Latest}.", installed, latest);
                    return;
                }

                logger.Info("FFmpeg update available: {Installed} -> {Latest} from {Repo}; downloading.", installed?.ToString() ?? "none", latest, FFmpegRelease.Repo);
                await DownloadBothAsync(release, asset, targetDir, logger, deadline.Token).ConfigureAwait(false);
                AudioMetadataHandler.ResetFFmpegInstallationCheck();
                logger.Info("FFmpeg updated to {Latest} at {Dir}.", latest, targetDir);
            }
            catch (OperationCanceledException)
            {
                // Shutdown or update deadline — leave the current binary in place, no noise.
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "FFmpeg auto-update check failed; keeping current binary.");
            }
            finally
            {
                _gate.Release();
            }
        }

        private static async Task DownloadBothAsync(FFmpegRelease.LatestRelease release, FFmpegRelease.PlatformAsset asset, string targetDir, Logger logger, CancellationToken ct)
        {
            string sums = await GetSumsAsync(release, ct).ConfigureAwait(false);
            Directory.CreateDirectory(targetDir);
            await DownloadVerifiedAsync(release, asset.FfmpegAsset, asset.LocalFfmpegName, targetDir, sums, ct).ConfigureAwait(false);
            await DownloadVerifiedAsync(release, asset.FfprobeAsset, asset.LocalFfprobeName, targetDir, sums, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Newest release that actually publishes <paramref name="asset"/>, which is not
        /// always the newest release — see <see cref="FFmpegRelease.ReleasesApiUrl"/>.
        /// </summary>
        private static async Task<FFmpegRelease.LatestRelease?> ResolveReleaseAsync(FFmpegRelease.PlatformAsset asset, CancellationToken ct)
        {
            string json = await _http.GetStringAsync(FFmpegRelease.ReleasesApiUrl, ct).ConfigureAwait(false);
            return FFmpegRelease.SelectForAsset(FFmpegRelease.ParseReleases(json), asset);
        }

        private static async Task<string> GetSumsAsync(FFmpegRelease.LatestRelease release, CancellationToken ct)
        {
            if (!release.AssetUrls.TryGetValue("SHA256SUMS", out string? url))
                throw new InvalidOperationException($"{FFmpegRelease.Repo} release {release.Tag} has no SHA256SUMS asset.");
            return await _http.GetStringAsync(url, ct).ConfigureAwait(false);
        }

        private static async Task DownloadVerifiedAsync(FFmpegRelease.LatestRelease release, string assetName, string localName, string targetDir, string sums, CancellationToken ct)
        {
            if (!release.AssetUrls.TryGetValue(assetName, out string? url))
                throw new InvalidOperationException($"{FFmpegRelease.Repo} release {release.Tag} is missing asset {assetName}.");

            byte[] data = await _http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
            if (!FFmpegRelease.VerifySha(data, FFmpegRelease.ExpectedSha(sums, assetName)))
                throw new InvalidOperationException($"SHA-256 verification failed for {assetName} from {FFmpegRelease.Repo} {release.Tag}.");

            string finalPath = Path.Combine(targetDir, localName);
            string tmpPath = finalPath + ".tmp-" + Guid.NewGuid().ToString("N");
            await File.WriteAllBytesAsync(tmpPath, data, ct).ConfigureAwait(false);
            try
            {
                SetExecutable(tmpPath);
                File.Move(tmpPath, finalPath, overwrite: true);
            }
            catch (Exception)
            {
                TryDelete(tmpPath);
                throw;
            }
        }

        private static void SetExecutable(string path)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return;

            try
            {
                UnixFileMode mode = File.GetUnixFileMode(path);
                File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
            catch (Exception ex)
            {
                NzbDroneLogger.GetLogger(typeof(FFmpegInstaller)).Debug(ex, "Failed to set +x on {Path}", path);
            }
        }

        private static DateTimeOffset? GetStampUtc(string stampPath)
        {
            try
            {
                return File.Exists(stampPath) ? File.GetLastWriteTimeUtc(stampPath) : null;
            }
            catch (Exception ex)
            {
                NzbDroneLogger.GetLogger(typeof(FFmpegInstaller)).Trace(ex, "Failed to read ffmpeg update stamp {Path}", stampPath);
                return null;
            }
        }

        private static void TouchStamp(string stampPath)
        {
            try
            {
                File.WriteAllText(stampPath, DateTimeOffset.UtcNow.ToString("O"));
            }
            catch (Exception ex)
            {
                NzbDroneLogger.GetLogger(typeof(FFmpegInstaller)).Debug(ex, "Failed to write ffmpeg update stamp {Path}", stampPath);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                NzbDroneLogger.GetLogger(typeof(FFmpegInstaller)).Trace(ex, "Failed to delete temp file {Path}", path);
            }
        }
    }
}
