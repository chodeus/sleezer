using System.IO;
using NLog;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;

/// <summary>Writes SOURCE=soulseek into the download's own audio files (Tag Files as Soulseek).</summary>
public static class SlskdSourceTag
{
    public const string Value = "soulseek";

    /// <summary>Tags each file the item owns by name and size; returns how many took the tag.</summary>
    public static int Write(SlskdDownloadItem item, IEnumerable<string> audioFiles, Logger logger, CancellationToken ct)
    {
        Dictionary<string, long> owned = item.BuildOwnedFileSizes();
        int written = 0;

        foreach (string path in audioFiles)
        {
            // Outside the try: the catch below would swallow the post-processing timeout.
            ct.ThrowIfCancellationRequested();
            string name = Path.GetFileName(path);
            try
            {
                // A shared download folder can hold another download's files; leave those alone.
                if (!owned.TryGetValue(name, out long size) || new FileInfo(path).Length != size)
                    continue;

                if (!SourceTagWriter.TryWrite(path, Value, logger))
                    continue;

                // Ownership checks match on size, which the tag write changes — see BuildOwnedFileSizes.
                item.RecordTaggedFile(name, new FileInfo(path).Length);
                written++;
            }
            catch (Exception ex)
            {
                // Never fail a finished download over provenance metadata (as SourceTagWriter).
                logger.Debug(ex, "Could not tag {Path} as Soulseek", path);
            }
        }

        return written;
    }
}
