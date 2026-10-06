using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using NLog;
using NzbDrone.Core.Download.Clients.Deezer.Queue;
using NzbDrone.Plugin.Sleezer.Core.Utilities;

namespace NzbDrone.Plugin.Sleezer.Core.Deezer
{
    /// <summary>Judges one Deezer track download attempt: whether its file is complete, and whether a failure is worth another try.</summary>
    public static class DeezerTrackAttempt
    {
        // More than 10% under Deezer's catalogued size is a dropped body; the MP3 320 fallback is measured against its own size.
        private const double PartialWriteThreshold = 0.9;

        public static bool ShouldRetry(Exception ex, int attempt, int maxAttempts) =>
            attempt < maxAttempts && IsTransient(ex);

        // Only a dropped connection, a stall, a 429 or 5xx, or a short file can change on another try.
        private static bool IsTransient(Exception ex) => ex switch
        {
            HttpRequestException { StatusCode: null } => true,
            HttpRequestException { StatusCode: { } status } => TransientStatus.IsTransient(status),
            IOException or TimeoutException or TrackIncompleteException => true,
            _ => false,
        };

        public static void EnsureCompleteFile(string outPath, string track, long expectedSize, Logger logger)
        {
            if (File.Exists(outPath) && new FileInfo(outPath).Length == 0)
            {
                File.Delete(outPath);
                throw new TrackIncompleteException($"Deezer returned an empty file for {track}.");
            }

            if (expectedSize <= 0 || !File.Exists(outPath))
                return;

            long actualSize = new FileInfo(outPath).Length;
            if (actualSize < expectedSize * PartialWriteThreshold)
            {
                File.Delete(outPath);
                throw new TrackIncompleteException(
                    $"Deezer {track} truncated: got {actualSize:N0} of expected {expectedSize:N0} bytes ({(double)actualSize / expectedSize:P0}).");
            }

            // Slightly short is tag stripping or the bitrate fallback; the corruption scan judges the audio.
            if (actualSize < expectedSize)
                logger.Trace("Deezer {Track}: got {Actual:N0} of expected {Expected:N0} bytes ({Pct:P0}); within tolerance.",
                    track, actualSize, expectedSize, (double)actualSize / expectedSize);
        }

        public static bool IsLicenseRightsError(Exception ex)
        {
            for (var cur = ex; cur != null; cur = cur.InnerException)
            {
                var msg = cur.Message ?? string.Empty;
                if (msg.Contains("License token has no sufficient rights", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (cur is AggregateException agg && agg.InnerExceptions.Any(IsLicenseRightsError))
                    return true;
            }
            return false;
        }

        public static bool IsGeoRestrictionError(Exception ex)
        {
            for (var cur = ex; cur != null; cur = cur.InnerException)
            {
                var msg = cur.Message ?? string.Empty;
                if (msg.Contains("not available in your country", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("wrong geolocation", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("geo-restricted", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (cur is AggregateException agg && agg.InnerExceptions.Any(IsGeoRestrictionError))
                    return true;
            }
            return false;
        }
    }
}
