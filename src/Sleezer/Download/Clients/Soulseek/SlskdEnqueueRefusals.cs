using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;

/// <summary>Sorts the files slskd refused to queue for a grab into those it can take over and those that refuse the grab.</summary>
public static class SlskdEnqueueRefusals
{
    /// <summary>
    /// A file slskd skipped is taken over from every superseded attempt, from the same peer into the same folder and not
    /// already being removed, that still holds its live transfer.
    /// </summary>
    public static (List<(SlskdDownloadItem Holder, string Filename)> TakenOver, List<SlskdEnqueueFailure> Refused) Split(
        IEnumerable<SlskdEnqueueFailure> failed,
        IReadOnlyCollection<SlskdDownloadItem> superseded,
        string username,
        string? destination)
    {
        List<(SlskdDownloadItem Holder, string Filename)> takenOver = [];
        List<SlskdEnqueueFailure> refused = [];
        foreach (SlskdEnqueueFailure failure in failed)
        {
            // slskd skips a file only while another transfer of it is live; "Error:" means it failed to start.
            List<SlskdDownloadItem> holders = destination == null || failure.Message?.StartsWith("Skipped:", StringComparison.OrdinalIgnoreCase) != true
                ? []
                : [.. superseded.Where(s => !s.Removed && s.Username == username && s.EnqueueDestination == destination && s.HoldsLiveTransfer(failure.Filename))];
            if (holders.Count == 0)
                refused.Add(failure);
            else
                takenOver.AddRange(holders.Select(h => (h, failure.Filename)));
        }

        return (takenOver, refused);
    }
}
