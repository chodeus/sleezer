namespace NzbDrone.Plugin.Sleezer.Core.PostProcessing
{
    /// <summary>Scans a folder's audio files under a CPU-bounded gate.</summary>
    internal static class CorruptionScanPass
    {
        public static async Task<(string Path, CorruptionScanner.Result Result)[]> RunAsync(
            ICorruptionScanner scanner,
            IReadOnlyList<string> audioFiles,
            CancellationToken ct)
        {
            // ffmpeg is CPU-bound, so half the cores leaves headroom for the rest of Lidarr.
            int concurrency = Math.Max(2, Environment.ProcessorCount / 2);
            using SemaphoreSlim gate = new(concurrency);

            Task<(string Path, CorruptionScanner.Result Result)>[] tasks = [.. audioFiles.Select(async path =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    return (path, await scanner.ScanAsync(path, PostProcessRunner.CorruptionScanTimeoutSeconds, ct));
                }
                finally
                {
                    gate.Release();
                }
            })];

            // Await everything before inspecting: iterating with await rethrows on the first
            // failure and disposes the gate while siblings are still parked on it.
            return await Task.WhenAll(tasks);
        }
    }
}
