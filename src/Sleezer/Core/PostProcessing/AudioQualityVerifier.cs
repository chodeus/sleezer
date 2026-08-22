using NLog;

namespace NzbDrone.Plugin.Sleezer.Core.PostProcessing
{
    public enum QualityVerdict
    {
        /// <summary>Nothing to compare — one side is missing or the format has no depth.</summary>
        Unknown,

        /// <summary>The files are what they were advertised as.</summary>
        Matches,

        /// <summary>The files disagree among themselves, so no single claim can be true.</summary>
        Mixed,

        /// <summary>The files are real but not what was advertised.</summary>
        Overstated
    }

    /// <summary>What the files on disk actually are, as opposed to what a peer said.</summary>
    public sealed record AudioQualityReading(int? BitDepth, int? SampleRate, int FilesRead, bool Mixed)
    {
        public static readonly AudioQualityReading None = new(null, null, 0, false);
    }

    /// <summary>
    /// Reads real bit depth and sample rate off downloaded files and compares them with
    /// what the source advertised. Soulseek peers advertise attributes nobody verifies;
    /// this is the only point at which the bytes can answer for themselves.
    /// </summary>
    public static class AudioQualityVerifier
    {
        /// <summary>
        /// Compares an advertised claim against a reading. Reports only — a file that is
        /// merely not what it claimed is still a good file, and Lidarr re-detects quality
        /// at import, so failing the item here would delete a usable album over a label.
        /// </summary>
        public static QualityVerdict Compare(int? advertisedDepth, int? advertisedRate, AudioQualityReading actual)
        {
            if (actual.Mixed)
                return QualityVerdict.Mixed;

            if (actual.FilesRead == 0 || actual.BitDepth is null or <= 0)
                return QualityVerdict.Unknown;

            if (advertisedDepth is null or <= 0)
                return QualityVerdict.Unknown;

            if (advertisedDepth != actual.BitDepth)
                return QualityVerdict.Overstated;

            // Depth agrees; only call the rate a mismatch when both sides stated one.
            if (advertisedRate is > 0 && actual.SampleRate is > 0 && advertisedRate != actual.SampleRate)
                return QualityVerdict.Overstated;

            return QualityVerdict.Matches;
        }

        /// <summary>
        /// Reads every file, returning the agreed depth/rate or Mixed when they differ.
        /// Files TagLib cannot open are skipped — the corruption scan owns that verdict.
        /// </summary>
        public static AudioQualityReading Read(IEnumerable<string> audioFiles, Logger logger)
        {
            int? depth = null;
            int? rate = null;
            int read = 0;
            bool mixed = false;

            foreach (string path in audioFiles)
            {
                (int? fileDepth, int? fileRate) = ReadOne(path, logger);
                if (fileDepth is null or <= 0)
                    continue;

                read++;

                if (depth is null)
                {
                    depth = fileDepth;
                    rate = fileRate;
                    continue;
                }

                if (fileDepth != depth || (fileRate is > 0 && rate is > 0 && fileRate != rate))
                    mixed = true;
            }

            return read == 0 ? AudioQualityReading.None : new AudioQualityReading(depth, rate, read, mixed);
        }

        private static (int? Depth, int? Rate) ReadOne(string path, Logger logger)
        {
            try
            {
                using TagLib.File file = TagLib.File.Create(path);

                // Lossy formats report 0 here; there is no depth to verify on them.
                int depth = file.Properties?.BitsPerSample ?? 0;
                int rate = file.Properties?.AudioSampleRate ?? 0;

                return (depth > 0 ? depth : null, rate > 0 ? rate : null);
            }
            catch (Exception ex)
            {
                logger.Trace(ex, "[quality-verify] Could not read audio properties from {File}", Path.GetFileName(path));
                return (null, null);
            }
        }
    }
}
