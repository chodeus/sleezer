using NzbDrone.Common.Disk;
using NzbDrone.Core.Download;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;

public interface ISlskdDownloadManager
{
    Task<string> DownloadAsync(RemoteAlbum remoteAlbum, int definitionId, SlskdProviderSettings settings);
    IEnumerable<DownloadClientItem> GetItems(int definitionId, SlskdProviderSettings settings, OsPath remotePath);
    void RemoveItem(DownloadClientItem clientItem, bool deleteData, int definitionId, SlskdProviderSettings settings);
    void ImportExtrasForImportedAlbum(string downloadId, IReadOnlyCollection<string> importedTrackPaths);

    /// <summary>Claims a grab's file under the name something outside this client renamed it to.</summary>
    void RecordRenamedFile(string downloadId, string originalPath, string newPath);

    /// <summary>Whether a local file is the grab's, by name and size; null when no grab has the id.</summary>
    bool? OwnsLocalFile(string downloadId, string path);
}
