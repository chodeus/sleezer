using System.Collections.Concurrent;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;

/// <summary>A grab Lidarr removed: which slskd transfers are still its own, and whether a poll has seen them all gone.</summary>
public sealed class SlskdRemovedGrab
{
    private static readonly TimeSpan CancelInterval = TimeSpan.FromSeconds(30);

    private readonly SlskdDownloadItem? _item;
    // Its last poll's transfers: an inclusive item has no batch to find them by.
    private readonly HashSet<string> _transferIds;
    private readonly ConcurrentDictionary<string, DateTime> _nextCancel = new();
    private int _settledBatches = -1;

    public SlskdRemovedGrab(SlskdDownloadItem? item, DateTime at)
    {
        _item = item;
        _transferIds = [.. (item?.SlskdDownloadDirectory?.Files ?? []).Where(f => item!.OwnsAcceptedFile(f.Filename)).Select(f => f.Id)];
        At = at;
        Username = item?.Username;
    }

    public DateTime At { get; }

    /// <summary>Lidarr removing a grab sleezer already untracked brings no item, so this record's transfers are kept.</summary>
    public SlskdRemovedGrab RemovedAgain(SlskdDownloadItem? item, DateTime at) => item == null ? this : new SlskdRemovedGrab(item, at);

    public string? Username { get; }

    public bool Settled => Username == null || Volatile.Read(ref _settledBatches) == BatchCount;

    /// <summary>How many batches it has enqueued; read before listing its peer, for <see cref="SettleUnlessListed"/>.</summary>
    public int BatchCount => _item?.EnqueuedBatches().Count ?? 0;

    /// <summary>The files enqueued in one of its batches, or seen by its last poll.</summary>
    public List<SlskdDownloadFile> Leftovers(IEnumerable<SlskdDownloadFile> files)
    {
        IReadOnlySet<string> batches = _item?.EnqueuedBatches() ?? new HashSet<string>();
        return [.. files.Where(f => (f.BatchId != null && batches.Contains(f.BatchId)) || _transferIds.Contains(f.Id))];
    }

    /// <summary>Settles once its peer's listing holds none of its transfers; a retry batch recorded since the count reopens it.</summary>
    public void SettleUnlessListed(List<SlskdDownloadFile> peerFiles, int batchesBeforeListing)
    {
        if (Leftovers(peerFiles).Count == 0)
            Volatile.Write(ref _settledBatches, batchesBeforeListing);
    }

    // Polls run every few seconds; a cancel that keeps failing must not be re-sent on each, nor hold up its other directories.
    public bool TryClaimCancel(string directory, DateTime now)
    {
        DateTime next = _nextCancel.GetOrAdd(directory, DateTime.MinValue);
        return now >= next && _nextCancel.TryUpdate(directory, now + CancelInterval, next);
    }
}
