using System.Collections.Concurrent;
using NLog;

namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    /// <summary>
    /// Independent janitor for the orphaned-empty-folder gap every download
    /// client shares: per-item cleanup only fires while an item is still
    /// tracked (a restart, a retention purge, or an importFailed item leaves the
    /// folder behind), and neither slskd nor Lidarr removes an empty directory.
    /// Prunes recursively-empty directories directly under a download root.
    ///
    /// Destructive by nature, so every guard fails closed:
    /// - emptiness counts hidden/system files and treats an unreadable subtree
    ///   as non-empty (<see cref="DirectoryEmptiness.IsTreeFileFree"/>);
    /// - deletion is bottom-up with recursive:false, which the OS refuses on any
    ///   non-empty directory, so a file that lands in the check→delete window is
    ///   never wiped;
    /// - symlinks/reparse points are skipped, not followed or deleted;
    /// - candidates are re-confined as strict descendants of the resolved root;
    /// - a folder written to within <paramref name="quietPeriod"/> or owned by a
    ///   tracked item is skipped.
    /// </summary>
    public static class EmptyDownloadDirectorySweeper
    {
        public static int Prune(string? root, IReadOnlyCollection<string> trackedLeaves, TimeSpan quietPeriod, DateTime nowUtc, Logger logger)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                return 0;

            string normalizedRoot;
            IEnumerable<string> children;
            try
            {
                normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                children = Directory.EnumerateDirectories(root).ToList();
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Empty download-directory sweep could not enumerate {Root}", root);
                return 0;
            }

            HashSet<string> tracked = trackedLeaves as HashSet<string> is { } h && ReferenceEquals(h.Comparer, StringComparer.OrdinalIgnoreCase)
                ? h
                : new HashSet<string>(trackedLeaves, StringComparer.OrdinalIgnoreCase);

            int pruned = 0;
            foreach (string dir in children)
            {
                string candidate = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                try
                {
                    if ((File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
                        continue;
                    if (!IsStrictDescendant(candidate, normalizedRoot))
                        continue;
                    if (tracked.Contains(Path.GetFileName(candidate)))
                        continue;
                    if (nowUtc - Directory.GetLastWriteTimeUtc(candidate) < quietPeriod)
                        continue;
                    if (!DirectoryEmptiness.IsTreeFileFree(candidate))
                        continue;

                    DeleteEmptyTree(candidate);
                    pruned++;
                    logger.Debug("Pruned empty stale download directory: {Dir}", candidate);
                }
                catch (Exception ex)
                {
                    logger.Warn(ex, "Failed to prune empty download directory {Dir}", candidate);
                }
            }

            return pruned;
        }

        /// <summary>
        /// The immediate child-of-root folder name a path lives under (the level
        /// the sweep prunes), or null if the path is not under root. Lets a
        /// client mark the root-child that owns an active download so it is
        /// never pruned.
        /// </summary>
        // Static so throttle + single-flight survive Lidarr's transient download-
        // client instances (a fresh client is resolved per poll; instance fields
        // would reset every time, defeating both). Keyed by a stable per-client
        // key (the download root).
        private static readonly ConcurrentDictionary<string, DateTime> _lastSweepByKey = new();
        private static readonly ConcurrentDictionary<string, byte> _inFlightByKey = new();

        /// <summary>
        /// Throttled, single-flight, off-thread prune for clients whose state
        /// cannot live on the (transient) client instance. Safe to call on every
        /// poll — at most one sweep per <paramref name="throttle"/> per key runs.
        /// </summary>
        public static bool MaybePruneThrottled(string key, string? root, IReadOnlyCollection<string> trackedLeaves, TimeSpan quietPeriod, TimeSpan throttle, DateTime nowUtc, Logger logger)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(root))
                return false;
            if (nowUtc - _lastSweepByKey.GetOrAdd(key, DateTime.MinValue) < throttle)
                return false;
            if (!_inFlightByKey.TryAdd(key, 0))
                return false;
            _lastSweepByKey[key] = nowUtc;

            _ = Task.Run(() =>
            {
                try { Prune(root, trackedLeaves, quietPeriod, nowUtc, logger); }
                catch (Exception ex) { logger.Warn(ex, "Empty download-directory sweep failed for {Key}", key); }
                finally { _inFlightByKey.TryRemove(key, out _); }
            });
            return true;
        }

        public static string? RootChildLeaf(string? root, string? path)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(path))
                return null;
            try
            {
                string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string full = Path.GetFullPath(path);
                string prefix = fullRoot + Path.DirectorySeparatorChar;
                if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || full.Length <= prefix.Length)
                    return null;
                string rel = full[prefix.Length..];
                int sep = rel.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
                return sep < 0 ? rel : rel[..sep];
            }
            catch
            {
                return null;
            }
        }

        private static bool IsStrictDescendant(string candidate, string normalizedRoot)
        {
            string full = Path.GetFullPath(candidate);
            string rootWithSep = normalizedRoot + Path.DirectorySeparatorChar;
            return full.Length > rootWithSep.Length
                && full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);
        }

        private static void DeleteEmptyTree(string dir)
        {
            foreach (string sub in Directory.EnumerateDirectories(dir))
            {
                if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0)
                    continue;
                DeleteEmptyTree(sub);
            }

            // recursive:false — the OS refuses to remove a non-empty directory,
            // so a file racing into the tree makes this throw rather than wipe it.
            Directory.Delete(dir, false);
        }
    }
}
