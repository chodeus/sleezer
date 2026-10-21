using NLog;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Plugin.Sleezer.Core.PostProcessing;

/// <summary>AcoustID lookups for a download's files, in batches AcoustID accepts.</summary>
public static class FingerprintLookup
{
    // AcoustID took 20 fingerprints in one request and refused 30 ("request too large"), gzipped as Lidarr sends them.
    public const int BatchSize = 10;

    /// <summary>
    /// Fills each LocalTrack.AcoustIdResults in place, a batch per request rather than one per file
    /// (AcoustID allows about 3 requests a second). A failed batch leaves its files unverifiable.
    /// </summary>
    public static async Task RunAsync(IFingerprintingService fingerprinting, IReadOnlyList<LocalTrack> tracks, double threshold, Logger logger, CancellationToken ct)
    {
        if (tracks.Count == 0 || !fingerprinting.IsSetup())
            return;

        foreach (LocalTrack[] batch in tracks.Chunk(BatchSize))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Lookup is synchronous with no CancellationToken (Lidarr core): off-thread, the wait stays cancellable.
                await Task.Run(() => fingerprinting.Lookup([.. batch], threshold)).WaitAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.Warn(ex, "Pre-import tag: fingerprint lookup failed for {Count} file(s); treating as unverifiable", batch.Length);
            }
        }
    }
}
