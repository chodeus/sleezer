using System.Collections.Concurrent;
using NLog;

namespace NzbDrone.Plugin.Sleezer.Indexers.Soulseek;

/// <summary>The users in an Ignore List file, re-read whenever the file changes.</summary>
public static class SlskdIgnoreList
{
    private static readonly ConcurrentDictionary<string, (HashSet<string> Users, long Size, DateTime WrittenUtc)> Cache = new();

    /// <summary>The listed users, or null when no list is set or none was ever read.</summary>
    public static HashSet<string>? Users(string? path, Logger logger)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        Cache.TryGetValue(path, out (HashSet<string> Users, long Size, DateTime WrittenUtc) cached);
        // An editor can delete the file before writing it back: the blocked peers stay blocked meanwhile.
        if (!File.Exists(path))
            return cached.Users;

        try
        {
            FileInfo file = new(path);
            if (cached.Users != null && cached.Size == file.Length && cached.WrittenUtc == file.LastWriteTimeUtc)
                return cached.Users;

            HashSet<string> users = SlskdTextProcessor.ParseListContent(File.ReadAllText(path));
            Cache[path] = (users, file.Length, file.LastWriteTimeUtc);
            logger.Trace("Loaded ignore list with {Count} users from {Path}", users.Count, path);
            return users;
        }
        catch (Exception ex)
        {
            // A list being saved can fail one read: the last one read keeps blocked peers blocked.
            logger.Warn(ex, "Failed to read the ignore list {Path}; {Action}", path, cached.Users != null ? "using the last one read" : "no users ignored");
            return cached.Users;
        }
    }

    public static void Invalidate(string path) => Cache.TryRemove(path, out _);
}
