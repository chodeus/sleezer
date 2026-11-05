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

    /// <summary>A batch still all empty after this long counts as given up (Lidarr: 3 s of refusal retries, 5 s timeout).</summary>
    // Times the whole Lookup call, so a first try slowed by fpcalc can count too; a retry reuses Lidarr's cached fingerprints.
    internal static readonly TimeSpan GaveUpAfter = TimeSpan.FromSeconds(2.5);

    // Lidarr's shared AcoustID key is refused in runs of several requests.
    internal const int MaxRetries = 3;

    private static readonly TimeSpan RetryPause = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Fills each LocalTrack.AcoustIdResults in place, a batch per request rather than one per file
    /// (AcoustID allows about 3 requests a second). A failed batch leaves its files unverifiable.
    /// </summary>
    public static Task RunAsync(IFingerprintingService fingerprinting, IReadOnlyList<LocalTrack> tracks, double threshold, Logger logger, CancellationToken ct) =>
        RunAsync(fingerprinting, tracks, threshold, logger, GaveUpAfter, RetryPause, ct);

    /// <summary>As above; a batch that came back empty only after <paramref name="gaveUp"/> is asked again, up to <see cref="MaxRetries"/> times <paramref name="retryPause"/> apart.</summary>
    internal static async Task RunAsync(IFingerprintingService fingerprinting, IReadOnlyList<LocalTrack> tracks, double threshold, Logger logger, TimeSpan gaveUp, TimeSpan retryPause, CancellationToken ct)
    {
        if (tracks.Count == 0 || !fingerprinting.IsSetup())
            return;

        bool retry = true;
        foreach (LocalTrack[] batch in tracks.Chunk(BatchSize))
        {
            ct.ThrowIfCancellationRequested();

            bool failed = GaveUp(await LookupAsync(fingerprinting, batch, threshold, logger, ct), gaveUp, batch);
            for (int attempt = 1; failed && retry && attempt <= MaxRetries; attempt++)
            {
                logger.Info("Pre-import tag: AcoustID refused or timed out on {Count} file(s); asking again in {Pause:F0}s ({Attempt} of {Max})",
                    batch.Length, retryPause.TotalSeconds, attempt, MaxRetries);
                await Task.Delay(retryPause, ct);
                failed = GaveUp(await LookupAsync(fingerprinting, batch, threshold, logger, ct), gaveUp, batch);
            }

            // AcoustID still down: the download's later batches don't wait out retries each.
            if (failed)
                retry = false;
        }
    }

    private static bool GaveUp(TimeSpan took, TimeSpan gaveUp, LocalTrack[] batch) =>
        took >= gaveUp && batch.All(t => t.AcoustIdResults == null);

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
