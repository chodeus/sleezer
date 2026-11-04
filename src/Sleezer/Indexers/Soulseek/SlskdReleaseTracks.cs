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

            if (Duplicate(audioFiles) is { } twice)
                return $"{twice} is in the folder twice";

            if (coveredTitles < wantedTitles)
                return $"{coveredTitles} of {wantedTitles} track titles found in the file names";

            List<int> expected = ReleaseSeconds(trackDurationsMs ?? []);
            int paired = Paired(expected, FileSeconds(audioFiles));
            return paired < expected.Count
                ? $"{paired} of {expected.Count} track lengths within {LengthToleranceSeconds}s of a file"
                : null;
        }

        // A tagger's padding block: copies of one lossless file that differ only by their tags are this close in size.
        private const long SameAudioSizeBytes = 8192;

        private static readonly HashSet<string> LosslessExtensions = new(StringComparer.OrdinalIgnoreCase) { ".flac", ".wav", ".aiff", ".aif", ".ape", ".wv" };

        /// <summary>A file that holds the same song as another in the folder, or null.</summary>
        // In two formats, or a lossless copy under another title; lossy files of one length are near one size anyway.
        internal static string? Duplicate(IReadOnlyCollection<SlskdFileData> audioFiles)
        {
            List<SlskdFileData> files = [.. audioFiles];
            for (int i = 0; i < files.Count; i++)
            {
                SlskdFileData? twin = files.Skip(i + 1).FirstOrDefault(other => SameSong(files[i], other));
                if (twin != null)
                    return Path.GetFileName(Normalised(twin));
            }

            return null;
        }

        private static bool SameSong(SlskdFileData a, SlskdFileData b) =>
            string.Equals(Path.ChangeExtension(Normalised(a), null), Path.ChangeExtension(Normalised(b), null), StringComparison.OrdinalIgnoreCase) ||
            (IsLossless(a) && IsLossless(b) && a.Length is > 0 && a.Length == b.Length && Math.Abs(a.Size - b.Size) <= SameAudioSizeBytes);

        private static bool IsLossless(SlskdFileData file) => LosslessExtensions.Contains(Path.GetExtension(Normalised(file)));

        // Peers send Windows paths, which Path won't split on Linux.
        private static string Normalised(SlskdFileData file) => (file.Filename ?? string.Empty).Replace('\\', '/');

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
