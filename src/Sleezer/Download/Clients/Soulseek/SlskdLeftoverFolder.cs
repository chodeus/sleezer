using System.Text;
using NzbDrone.Common.Disk;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;

/// <summary>Every attempt at an album shares its folder; an earlier attempt's files there would sit beside the new grab's.</summary>
public static class SlskdLeftoverFolder
{
    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or inside it (a disc folder), as the filesystem compares names.</summary>
    public static bool Contains(string folder, string path)
    {
        string key = Key(FullPath(folder));
        string candidate = Key(FullPath(path));
        StringComparison comparison = DiskProviderBase.PathStringComparison;
        return string.Equals(candidate, key, comparison) || candidate.StartsWith(key + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>Renames a non-empty folder aside; returns where it went, or null when there was nothing to move.</summary>
    public static string? MoveAside(IDiskProvider disk, string folder, DateTime utcNow)
    {
        if (!disk.FolderExists(folder) || disk.FolderEmpty(folder))
            return null;

        // Renamed, not deleted: it stays until slskd's file retention, if configured, or a manual cleanup clears it.
        string target = FullPath(folder);
        string aside = $"{target} (leftover {utcNow:yyyyMMdd-HHmmss})";
        disk.MoveFolder(target, aside);
        return aside;
    }

    private static string FullPath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    // macOS treats composed and decomposed accents as one name; Linux and Windows keep them as two folders.
    private static string Key(string path) => OperatingSystem.IsMacOS() ? path.Normalize(NormalizationForm.FormC) : path;
}
