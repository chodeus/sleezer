using NLog;

namespace NzbDrone.Plugin.Sleezer.Core.PostProcessing;

/// <summary>Copies of a download's files taken before tagging, so a pass that can't finish puts every file back.</summary>
// Lidarr's tag writer logs a failed save and returns, so a write that didn't happen only shows up on disk.
public sealed class TagBackups : IDisposable
{
    private const string Suffix = ".sleezer-untagged";
    private readonly Dictionary<string, string> _copies = new(StringComparer.Ordinal);
    private readonly Logger _logger;

    private TagBackups(Logger logger)
    {
        _logger = logger;
    }

    /// <summary>A copy of every file, or null, keeping none, when any copy fails.</summary>
    public static TagBackups? Of(IEnumerable<string> paths, Logger logger)
    {
        TagBackups backups = new(logger);
        try
        {
            foreach (string path in paths)
            {
                string copy = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, "." + Path.GetFileName(path) + Suffix);
                File.Copy(path, copy, overwrite: true);
                backups._copies[path] = copy;
            }

            return backups;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Warn(ex, "Pre-import tag: could not back up the files before tagging");
            backups.Discard();
            return null;
        }
    }

    /// <summary>Drops the copies once the tagged files are kept.</summary>
    public void Discard()
    {
        foreach (string copy in _copies.Values)
        {
            try
            {
                File.Delete(copy);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover copy has no audio extension, so Lidarr never imports it.
                _logger.Debug(ex, "Pre-import tag: could not remove backup {Copy}", copy);
            }
        }

        _copies.Clear();
    }

    /// <summary>Puts every original back, then removes the files a rename left under new names.</summary>
    public void Restore(IEnumerable<string> renamedPaths)
    {
        HashSet<string> originals = new(_copies.Keys, StringComparer.Ordinal);
        foreach ((string original, string copy) in _copies.ToList())
        {
            File.Move(copy, original, overwrite: true);
            _copies.Remove(original);
        }

        // Only now: until every original is back, a renamed file may be the only copy of its audio.
        foreach (string renamed in renamedPaths.Where(p => !originals.Contains(p) && File.Exists(p)))
            File.Delete(renamed);
    }

    // Never deletes: a copy still held here is what a failed restore left, and may be the only original.
    public void Dispose()
    {
        if (_copies.Count > 0)
            _logger.Warn("Pre-import tag: kept {Count} backup(s) of the files as downloaded after a failed restore: {Copies}", _copies.Count, string.Join(", ", _copies.Values));
    }
}
