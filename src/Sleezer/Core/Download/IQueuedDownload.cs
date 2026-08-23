using NzbDrone.Core.Download;

namespace NzbDrone.Plugin.Sleezer.Core.Download
{
    /// The part of a client's download item that <see cref="DownloadPump{TItem}"/> needs.
    public interface IQueuedDownload
    {
        string ID { get; }
        string Title { get; }
        DownloadItemStatus Status { get; set; }
    }
}
