using NLog;
using NzbDrone.Plugin.Sleezer.Core.Model;
using XabeFFmpeg = Xabe.FFmpeg.FFmpeg;

namespace NzbDrone.Plugin.Sleezer.Core.PostProcessing
{
    /// <summary>
    /// Points Xabe.FFmpeg at the configured directory and fetches the binaries when they
    /// are missing, so the corruption scan has an ffmpeg to call.
    /// </summary>
    public sealed class FFmpegPathResolver(Logger logger)
    {
        // null = never resolved. Lets a mid-run FFmpeg path change be picked up.
        private string? _lastResolvedPath;

        public async Task ResolveAsync(string? configuredPath, CancellationToken ct)
        {
            // Throttled (24h), best-effort refresh from chodeus/ffmpeg-static. Awaited
            // rather than detached: a detached task outlives the pass that started it.
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                try
                {
                    await FFmpegInstaller.EnsureUpToDateAsync(configuredPath, logger, ct);
                }
                catch (Exception ex)
                {
                    logger.Debug(ex, "[post-process] FFmpeg update check failed");
                }
            }

            // Re-resolve only when the path actually changes; the probe below hits disk.
            if (string.Equals(configuredPath, _lastResolvedPath, StringComparison.Ordinal))
                return;

            try
            {
                if (string.IsNullOrWhiteSpace(configuredPath))
                {
                    logger.Debug("[post-process] No FFmpeg path configured; the corruption scan will skip files it cannot decode.");
                }
                else
                {
                    XabeFFmpeg.SetExecutablesPath(configuredPath);
                    AudioMetadataHandler.ResetFFmpegInstallationCheck();

                    // Self-healing: ffmpeg can vanish under a rebuilt container.
                    if (!AudioMetadataHandler.CheckFFmpegInstalled())
                    {
                        logger.Info("[post-process] FFmpeg binaries missing at {Path}; downloading from chodeus/ffmpeg-static", configuredPath);
                        await AudioMetadataHandler.InstallFFmpeg(configuredPath);
                        AudioMetadataHandler.ResetFFmpegInstallationCheck();
                    }

                    logger.Info("[post-process] FFmpeg path applied: {Path}", configuredPath);
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "[post-process] Failed to apply ffmpeg path {Path}; the corruption scan may not run", configuredPath);
            }

            _lastResolvedPath = configuredPath;
        }
    }
}
