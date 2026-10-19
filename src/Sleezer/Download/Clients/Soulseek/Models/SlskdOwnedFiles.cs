using NzbDrone.Plugin.Sleezer.Core.Utilities;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;

/// <summary>A download's own files by local basename: every size it accepted for the name, or the size a tag write left.</summary>
public sealed class SlskdOwnedFiles(Dictionary<string, HashSet<long>> sizesByName, IReadOnlyList<string> acceptedNames)
{
    /// <summary>How many files the download accepted, extras included; two disc folders' "01.flac" count twice.</summary>
    public int FileCount { get; } = acceptedNames.Count;

    /// <summary>How many of those are audio, the files the folder-recovery majority rule counts.</summary>
    public int AudioFileCount { get; } = acceptedNames.Count(AudioFormatHelper.IsPostProcessAudioFile);

    /// <summary>Whether a file of this basename and size is one of the download's own.</summary>
    public bool Owns(string basename, long size) => sizesByName.TryGetValue(basename, out HashSet<long>? sizes) && sizes.Contains(size);

    /// <summary>The sizes owned under a basename, empty when the name is not the download's.</summary>
    public IReadOnlyCollection<long> SizesOf(string basename) => sizesByName.TryGetValue(basename, out HashSet<long>? sizes) ? sizes : [];

    /// <summary>The final paths of tagged files that were the download's own before tagging; the tagger also touches other downloads' files.</summary>
    public static IEnumerable<string> OwnTaggedFiles(IEnumerable<(string OriginalPath, string FinalPath)> tagged, IReadOnlySet<string> ownedPathsBeforeTagging) =>
        tagged.Where(t => ownedPathsBeforeTagging.Contains(t.OriginalPath)).Select(t => t.FinalPath);
}
