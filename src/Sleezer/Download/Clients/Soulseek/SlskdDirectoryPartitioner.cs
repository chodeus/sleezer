using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;

/// <summary>One item's share of a peer directory, and the files no tracked item claimed.</summary>
public record SlskdDirectoryPartition(
    List<(SlskdDownloadItem Item, SlskdDownloadDirectory Slice)> Owners,
    SlskdDownloadDirectory? Unclaimed);

/// <summary>
/// Splits one peer directory's transfers into per-item slices. Two Lidarr albums
/// can share a peer directory, so the group belongs to every item that enqueued
/// part of it — not to whichever item happens to own the first file.
/// </summary>
public static class SlskdDirectoryPartitioner
{
    /// <summary>Splits a peer directory into one slice per owning item, plus the rest.</summary>
    public static SlskdDirectoryPartition Partition(
        SlskdDownloadDirectory directory,
        IEnumerable<SlskdDownloadItem> candidates,
        string username)
    {
        List<SlskdDownloadFile> files = directory.Files ?? [];
        if (files.Count == 0)
            return new SlskdDirectoryPartition([], null);

        List<(SlskdDownloadItem Item, SlskdDownloadDirectory Slice)> owners = [];
        HashSet<string> claimed = new(StringComparer.OrdinalIgnoreCase);

        foreach (SlskdDownloadItem candidate in candidates)
        {
            // An item with no username yet is still a candidate for any peer.
            if (candidate.Username != null &&
                !string.Equals(candidate.Username, username, StringComparison.OrdinalIgnoreCase))
                continue;

            // Deliberately not deduped against `claimed`: two items can legitimately
            // enqueue the same remote file, and one transfer serves both.
            List<SlskdDownloadFile> owned = files.Where(f => candidate.OwnsAcceptedFile(f.Filename)).ToList();
            if (owned.Count == 0)
                continue;

            owners.Add((candidate, new SlskdDownloadDirectory(directory.Directory, owned.Count, owned)));
            foreach (SlskdDownloadFile file in owned)
                claimed.Add(file.Filename);
        }

        List<SlskdDownloadFile> unclaimed = files.Where(f => !claimed.Contains(f.Filename)).ToList();

        return new SlskdDirectoryPartition(
            owners,
            unclaimed.Count > 0 ? new SlskdDownloadDirectory(directory.Directory, unclaimed.Count, unclaimed) : null);
    }

    /// <summary>Adds the item whose ID hashes the whole directory, if it isn't already an owner.</summary>
    public static List<(SlskdDownloadItem Item, SlskdDownloadDirectory Slice)> WithKeyedOwner(
        SlskdDirectoryPartition partition,
        SlskdDownloadItem? keyed,
        SlskdDownloadDirectory directory)
    {
        List<(SlskdDownloadItem Item, SlskdDownloadDirectory Slice)> owners = [.. partition.Owners];

        // Returning early on the hash match would starve a co-owner that enqueued
        // only part of the directory — the same starvation this class exists to fix.
        if (keyed != null && !owners.Any(owner => ReferenceEquals(owner.Item, keyed)))
            owners.Add((keyed, directory));

        return owners;
    }
}
