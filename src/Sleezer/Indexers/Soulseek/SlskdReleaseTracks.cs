namespace NzbDrone.Plugin.Sleezer.Indexers.Soulseek
{
    /// <summary>Compares a peer folder's audio files with the searched release's tracks.</summary>
    internal static class SlskdReleaseTracks
    {
        // The Match Release Tracks help text in SlskdSettings quotes this value.
        internal const int LengthToleranceSeconds = 8;

        /// <summary>Why the files are not the release's tracks, or null when count, titles and lengths all agree.</summary>
        internal static string? Mismatch(IReadOnlyCollection<SlskdFileData> audioFiles, int trackCount, int coveredTitles, int wantedTitles, IReadOnlyList<int>? trackDurationsMs)
        {
            if (trackCount <= 0)
                return "the release's track count is unknown";

            if (audioFiles.Count != trackCount)
                return $"{audioFiles.Count} audio file(s) for {trackCount} track(s)";

            if (coveredTitles < wantedTitles)
                return $"{coveredTitles} of {wantedTitles} track titles found in the file names";

            List<int> expected = ReleaseSeconds(trackDurationsMs ?? []);
            int paired = Paired(expected, FileSeconds(audioFiles));
            return paired < expected.Count
                ? $"{paired} of {expected.Count} track lengths within {LengthToleranceSeconds}s of a file"
                : null;
        }

        /// <summary>The release's known track lengths in whole seconds, ascending.</summary>
        internal static List<int> ReleaseSeconds(IEnumerable<int> trackDurationsMs) =>
            [.. trackDurationsMs.Where(ms => ms > 0).Select(ms => (int)Math.Round(ms / 1000.0)).Order()];

        /// <summary>The lengths the peer reports for the files, ascending.</summary>
        internal static List<int> FileSeconds(IEnumerable<SlskdFileData> files) =>
            [.. files.Select(f => f.Length).OfType<int>().Where(s => s > 0).Order()];

        /// <summary>How many lengths pair within the tolerance, each used once. Both lists must be ascending.</summary>
        internal static int Paired(List<int> expected, List<int> actual)
        {
            int matched = 0;
            for (int e = 0, a = 0; e < expected.Count && a < actual.Count;)
            {
                int diff = actual[a] - expected[e];
                if (Math.Abs(diff) <= LengthToleranceSeconds)
                {
                    matched++;
                    e++;
                    a++;
                }
                else if (diff < 0)
                {
                    a++;
                }
                else
                {
                    e++;
                }
            }

            return matched;
        }
    }
}
