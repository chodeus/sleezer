namespace NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;

/// <summary>A download's own files by local basename: every size it accepted for the name, or the size a tag write left.</summary>
public sealed class SlskdOwnedFiles(Dictionary<string, HashSet<long>> sizesByName, int fileCount)
{
    /// <summary>How many files the download owns; two disc folders' "01.flac" count twice.</summary>
    public int FileCount { get; } = fileCount;

    /// <summary>Whether a file of this basename and size is one of the download's own.</summary>
    public bool Owns(string basename, long size) => sizesByName.TryGetValue(basename, out HashSet<long>? sizes) && sizes.Contains(size);

    /// <summary>The sizes owned under a basename, empty when the name is not the download's.</summary>
    public IReadOnlyCollection<long> SizesOf(string basename) => sizesByName.TryGetValue(basename, out HashSet<long>? sizes) ? sizes : [];
}
