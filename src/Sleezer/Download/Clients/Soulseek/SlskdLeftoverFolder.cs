using System.Text;
using NzbDrone.Common.Disk;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;

/// <summary>Every attempt at an album shares its folder; an untracked attempt's files there would sit beside the new grab's.</summary>
public static class SlskdLeftoverFolder
{
    /// <summary>Renames a non-empty folder that no tracked grab resolves to; returns where it went, or null when left alone.</summary>
    public static string? MoveAside(IDiskProvider disk, string folder, IEnumerable<string> trackedFolders, DateTime utcNow)
    {
        if (!disk.FolderExists(folder) || disk.FolderEmpty(folder))
            return null;

        // A tracked grab can resolve to a subfolder (a disc folder). Windows ignores case, and macOS treats composed and
        // decomposed accents as one name; either way the tracked folder is protected.
        string target = FullPath(folder);
        string key = Key(target);
        StringComparison comparison = DiskProviderBase.PathStringComparison;
        if (trackedFolders.Select(t => Key(FullPath(t))).Any(t => string.Equals(t, key, comparison) || t.StartsWith(key + Path.DirectorySeparatorChar, comparison)))
            return null;

        // Renamed, not deleted: it stays until slskd's file retention, if configured, or a manual cleanup clears it.
        string aside = $"{target} (leftover {utcNow:yyyyMMdd-HHmmss})";
        disk.MoveFolder(target, aside);
        return aside;
    }

    private static string FullPath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string Key(string path) => path.Normalize(NormalizationForm.FormC);
}
