using NLog;

namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    /// <summary>
    /// Removes the empty artist/album shells a download client leaves above a folder
    /// it has just deleted. Lidarr's DeleteItemData only removes the album folder it
    /// was given and never walks up.
    /// </summary>
    public static class DownloadFolderCleanup
    {
        /// <summary>
        /// Walks upward from <paramref name="startedAt"/> (already removed) deleting each
        /// now-empty parent, stopping at <paramref name="downloadRoot"/> or the first
        /// parent that still holds anything. Every failure stops the sweep, never throws.
        /// </summary>
        public static void TryRemoveEmptyParentFolders(string startedAt, string downloadRoot, string clientName, Logger logger)
        {
            try
            {
                string normalizedRoot = Path.GetFullPath(downloadRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string? current = Path.GetDirectoryName(Path.GetFullPath(startedAt));

                while (!string.IsNullOrEmpty(current))
                {
                    string normalizedCurrent = current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                    // Never delete at or above the configured download root. The
                    // separator matters: a bare StartsWith would treat
                    // "/downloads/music-old" as inside "/downloads/music", and this
                    // guard is the only thing standing in front of a directory delete.
                    if (normalizedCurrent.Length <= normalizedRoot.Length ||
                        !normalizedCurrent.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        return;

                    if (!Directory.Exists(current))
                        return;

                    // Stop at the first parent with anything in it (files OR unrelated
                    // subfolders from another grab).
                    if (Directory.EnumerateFileSystemEntries(current).Any())
                        return;

                    try
                    {
                        Directory.Delete(current, recursive: false);
                        logger.Debug("{Client}: removed empty parent folder {Folder}", clientName, current);
                    }
                    catch (Exception ex)
                    {
                        logger.Debug(ex, "{Client}: could not remove empty parent {Folder}; stopping sweep", clientName, current);
                        return;
                    }

                    current = Path.GetDirectoryName(current);
                }
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "{Client}: empty-parent sweep aborted from {Start}", clientName, startedAt);
            }
        }
    }
}
