using System.Diagnostics;
using NLog;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Plugin.Sleezer.Core.PostProcessing;

/// <summary>AcoustID lookups for a download's files, in batches AcoustID accepts.</summary>
public static class FingerprintLookup
{
    // AcoustID took 20 fingerprints in one request and refused 30 ("request too large"), gzipped as Lidarr sends them.
    public const int BatchSize = 10;

    /// <summary>Lidarr's AcoustID request timeout; it drops a timed-out answer with only a warning.</summary>
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan RetryPause = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Fills each LocalTrack.AcoustIdResults in place, a batch per request rather than one per file
    /// (AcoustID allows about 3 requests a second). A failed batch leaves its files unverifiable.
    /// </summary>
    public static Task RunAsync(IFingerprintingService fingerprinting, IReadOnlyList<LocalTrack> tracks, double threshold, Logger logger, CancellationToken ct) =>
        RunAsync(fingerprinting, tracks, threshold, logger, RequestTimeout, RetryPause, ct);

    /// <summary>As above; a batch that came back empty only after <paramref name="timedOut"/> is asked once more after <paramref name="retryPause"/>.</summary>
    internal static async Task RunAsync(IFingerprintingService fingerprinting, IReadOnlyList<LocalTrack> tracks, double threshold, Logger logger, TimeSpan timedOut, TimeSpan retryPause, CancellationToken ct)
    {
        if (tracks.Count == 0 || !fingerprinting.IsSetup())
            return;

        bool retry = true;
        foreach (LocalTrack[] batch in tracks.Chunk(BatchSize))
        {
            ct.ThrowIfCancellationRequested();

            // An answer comes back sooner; only a timeout leaves every file empty this late.
            if (!TimedOut(await LookupAsync(fingerprinting, batch, threshold, logger, ct), timedOut, batch) || !retry)
                continue;

            logger.Info("Pre-import tag: AcoustID gave nothing for {Count} file(s) after a timeout; asking once more in {Pause:F0}s",
                batch.Length, retryPause.TotalSeconds);
            await Task.Delay(retryPause, ct);

            // AcoustID still down: the download's later batches don't wait out a retry each.
            retry = !TimedOut(await LookupAsync(fingerprinting, batch, threshold, logger, ct), timedOut, batch);
        }
    }

    private static bool TimedOut(TimeSpan took, TimeSpan timedOut, LocalTrack[] batch) =>
        took >= timedOut && batch.All(t => t.AcoustIdResults == null);

    private static async Task<TimeSpan> LookupAsync(IFingerprintingService fingerprinting, LocalTrack[] batch, double threshold, Logger logger, CancellationToken ct)
    {
        Stopwatch watch = Stopwatch.StartNew();
        try
        {
            // Lookup is synchronous with no CancellationToken (Lidarr core): off-thread, the wait stays cancellable.
            await Task.Run(() => fingerprinting.Lookup([.. batch], threshold)).WaitAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warn(ex, "Pre-import tag: fingerprint lookup failed for {Count} file(s); treating as unverifiable", batch.Length);
        }

        return watch.Elapsed;
    }
}
