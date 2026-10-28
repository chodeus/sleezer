using NzbDrone.Common.Disk;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;

/// <summary>
/// A grab's destination is named after the album, so every attempt at it shares one folder. Files an
/// untracked earlier attempt left there would sit beside the new grab's, and slskd suffixes a clashing name.
/// </summary>
public static class SlskdLeftoverFolder
{
    /// <summary>Renames a non-empty folder that no tracked grab resolves to; returns where it went, or null when left alone.</summary>
    public static string? MoveAside(IDiskProvider disk, string folder, IEnumerable<string> trackedFolders, DateTime utcNow)
    {
        if (!disk.FolderExists(folder) || disk.FolderEmpty(folder))
            return null;

        // A tracked grab can resolve to a subfolder (a disc folder), and Windows paths ignore case.
        string target = Normalize(folder);
        StringComparison comparison = DiskProviderBase.PathStringComparison;
        if (trackedFolders.Select(Normalize).Any(t => string.Equals(t, target, comparison) || t.StartsWith(target + Path.DirectorySeparatorChar, comparison)))
            return null;

        // Renamed, not deleted: slskd's file retention clears it, and the empty-folder sweep then removes it.
        string aside = $"{target} (leftover {utcNow:yyyyMMdd-HHmmss})";
        disk.MoveFolder(target, aside);
        return aside;
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
