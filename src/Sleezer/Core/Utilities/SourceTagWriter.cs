using System;
using NLog;
using TagLib;

namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    public static class SourceTagWriter
    {
        /// Records where a download came from, as a Xiph SOURCE field. Lidarr's AudioTag
        /// has no URL of its own, so writeAudioTags=sync leaves this alone rather than
        /// overwriting it the way it does title, artist and media.
        public static void TryWrite(string filePath, string? sourceUrl, Logger logger)
        {
            if (string.IsNullOrWhiteSpace(sourceUrl) || !System.IO.File.Exists(filePath))
                return;

            try
            {
                using TagLib.File file = TagLib.File.Create(filePath);

                // Non-Xiph containers (MP3, M4A) return null rather than throwing, so
                // lossy downloads simply carry no source field.
                if (file.GetTag(TagTypes.Xiph, true) is not TagLib.Ogg.XiphComment xiph)
                    return;

                xiph.SetField("SOURCE", sourceUrl);
                file.Save();
            }
            catch (Exception ex)
            {
                // Never fail a finished download over provenance metadata.
                logger.Debug(ex, "Could not write the source URL to {Path}", filePath);
            }
        }
    }
}
