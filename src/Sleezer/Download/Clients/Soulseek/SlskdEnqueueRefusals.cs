using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;

/// <summary>Sorts the files slskd refused to queue for a grab into those it can take over and those that refuse the grab.</summary>
public static class SlskdEnqueueRefusals
{
    /// <summary>A refused file is taken over when a superseded attempt, from the same peer into the same folder, still holds its live transfer.</summary>
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
            SlskdDownloadItem? holder = destination == null ? null : superseded.FirstOrDefault(s =>
                s.Username == username && s.EnqueueDestination == destination && s.HoldsLiveTransfer(failure.Filename));
            if (holder == null)
                refused.Add(failure);
            else
                takenOver.Add((holder, failure.Filename));
        }

        return (takenOver, refused);
    }
}
