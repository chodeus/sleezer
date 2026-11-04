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
                // A short name of its own: a long track name plus a suffix can pass the filesystem's limit.
                string copy = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, "." + Guid.NewGuid().ToString("N") + Suffix);
                File.Copy(path, copy);
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

    /// <summary>Moves every copy back over its file, trying them all; throws when any stays tagged.</summary>
    public void Restore()
    {
        Exception? firstFailure = null;
        foreach ((string original, string copy) in _copies.ToList())
        {
            try
            {
                File.Move(copy, original, overwrite: true);
                _copies.Remove(original);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                firstFailure ??= ex;
            }
        }

        if (firstFailure != null)
            throw new TagRestoreException($"Pre-import tag: {_copies.Count} file(s) could not be put back as downloaded", firstFailure);
    }

    // Never deletes: a copy still held here is what a failed restore left, and is that file's only original.
    public void Dispose()
    {
        if (_copies.Count > 0)
            _logger.Warn("Pre-import tag: kept {Count} backup(s) of the files as downloaded after a failed restore: {Copies}",
                _copies.Count, string.Join(", ", _copies.Select(c => $"{c.Value} = {c.Key}")));
    }
}
