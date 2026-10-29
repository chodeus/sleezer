using System.Text;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;

/// <summary>Every attempt at an album shares its folder; an earlier attempt's files there would sit beside the new grab's.</summary>
public static class SlskdLeftoverFolder
{
    // ext4, XFS and APFS cap a name at 255 bytes.
    private const int MaxNameBytes = 255;

    // A case- or accent-insensitive volume (APFS, SMB) can sit behind any OS; a false match only holds a grab back.
    private const StringComparison NameComparison = StringComparison.OrdinalIgnoreCase;

    /// <summary>Compares folders as <see cref="Contains"/> matches one exactly.</summary>
    public static IEqualityComparer<string> FolderComparer { get; } = new PathComparer();

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or inside it (a disc folder), ignoring case and accent form.</summary>
    public static bool Contains(string folder, string path)
    {
        string key = Key(FullPath(folder));
        string candidate = Key(FullPath(path));
        return string.Equals(candidate, key, NameComparison) || candidate.StartsWith(key + Path.DirectorySeparatorChar, NameComparison);
    }

    /// <summary>Whether either folder is the other or inside it.</summary>
    public static bool Overlaps(string a, string b) => Contains(a, b) || Contains(b, a);

    /// <summary>Each client whose download root holds <paramref name="folder"/> or lies inside it, with its grabs inside it; one slskd can sit behind several clients.</summary>
    public static List<(int DefinitionId, List<SlskdDownloadItem> Owners)> ClientsHolding(
        string folder,
        IEnumerable<(int DefinitionId, OsPath Root, IEnumerable<SlskdDownloadItem> Items)> clients) =>
        [.. clients
            .Where(c => RootReaches(c.Root, folder))
            .Select(c => (c.DefinitionId, c.Items.Where(i => Contains(folder, i.GetFullFolderPath(c.Root).FullPath)).ToList()))];

    /// <summary>The folder a grab resolves to; null while it is a guess.</summary>
    public static string? KnownFolder(SlskdDownloadItem grab, OsPath root) => grab.FolderUnknown ? null : grab.GetFullFolderPath(root).FullPath;

    /// <summary>Whether a tracked grab may use the folder: one resolves to it, inside it or above it, or its folder is a guess. Null while a client reaching it hasn't loaded slskd's transfers.</summary>
    public static bool? InUse(string folder, IEnumerable<(int DefinitionId, OsPath Root, IEnumerable<SlskdDownloadItem> Items)> clients, Func<int, bool> loaded)
    {
        List<(int DefinitionId, OsPath Root, IEnumerable<SlskdDownloadItem> Items)> reaching = [.. clients.Where(c => RootReaches(c.Root, folder))];
        if (reaching.Any(c => c.Items.Any(i => i.FolderUnknown || Overlaps(folder, i.GetFullFolderPath(c.Root).FullPath))))
            return true;

        return reaching.All(c => loaded(c.DefinitionId)) ? false : null;
    }

    /// <summary>The grab still using the folder: its transfers run, or Lidarr isn't finished with it and the new grab doesn't replace it.</summary>
    public static SlskdDownloadItem? Holder(IEnumerable<SlskdDownloadItem> owners, IReadOnlyCollection<SlskdDownloadItem> replaced, Func<SlskdDownloadItem, bool> finishedInLidarr) =>
        owners.FirstOrDefault(i => i.HasLiveTransfers() || (!replaced.Contains(i) && !finishedInLidarr(i)));

    /// <summary>Whether the folder has anything in it to move aside.</summary>
    public static bool HoldsFiles(IDiskProvider disk, string folder) => disk.FolderExists(folder) && !disk.FolderEmpty(folder);

    /// <summary>Renames a non-empty folder aside; returns where it went, or null when there was nothing to move.</summary>
    public static string? MoveAside(IDiskProvider disk, string folder, DateTime utcNow)
    {
        if (!HoldsFiles(disk, folder))
            return null;

        // Renamed, not deleted: it stays until slskd's file retention, if configured, or a manual cleanup clears it.
        string target = FullPath(folder);
        string suffix = $" (leftover {utcNow:yyyyMMdd-HHmmss})";
        string aside = Path.Combine(Path.GetDirectoryName(target) ?? string.Empty, Shorten(Path.GetFileName(target), MaxNameBytes - Encoding.UTF8.GetByteCount(suffix)) + suffix);
        disk.MoveFolder(target, aside);
        return aside;
    }

    // A long album name gives up room for the suffix.
    private static string Shorten(string name, int maxBytes)
    {
        while (Encoding.UTF8.GetByteCount(name) > maxBytes)
            name = name[..^(name.Length > 1 && char.IsLowSurrogate(name[^1]) ? 2 : 1)];
        return name.TrimEnd();
    }

    private static bool RootReaches(OsPath root, string folder) =>
        root.FullPath.IsPathValid(PathValidationType.CurrentOs) && Overlaps(root.FullPath, folder);

    private static string FullPath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string Key(string path) => path.Normalize(NormalizationForm.FormC);

    private sealed class PathComparer : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y) =>
            x == null || y == null ? x == y : string.Equals(Key(FullPath(x)), Key(FullPath(y)), NameComparison);

        public int GetHashCode(string path) => Key(FullPath(path)).GetHashCode(NameComparison);
    }
}
