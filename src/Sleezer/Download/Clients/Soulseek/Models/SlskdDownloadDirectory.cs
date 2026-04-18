using System.Text.Json;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;

public record SlskdDownloadDirectory(string Directory, int FileCount, List<SlskdDownloadFile>? Files)
{
    public static IEnumerable<SlskdDownloadDirectory> GetDirectories(JsonElement directoriesElement)
    {
        if (directoriesElement.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (JsonElement directory in directoriesElement.EnumerateArray())
        {
            yield return new SlskdDownloadDirectory(
                Directory: directory.TryGetProperty("directory", out JsonElement d) ? d.GetString() ?? string.Empty : string.Empty,
                FileCount: directory.TryGetProperty("fileCount", out JsonElement fc) ? fc.GetInt32() : 0,
                Files: directory.TryGetProperty("files", out JsonElement files) ? SlskdDownloadFile.GetFiles(files).ToList() : []
            );
        }
    }

    public List<SlskdFileData> ToSlskdFileDataList() =>
        Files?.Select(f => f.ToSlskdFileData()).ToList() ?? [];

    public SlskdFolderData CreateFolderData(string username, ISlskdItemsParser slskdItemsParser) =>
        slskdItemsParser.ParseFolderName(Directory) with
        {
            Username = username,
            HasFreeUploadSlot = true,
            UploadSpeed = 0,
            LockedFileCount = 0,
            LockedFiles = []
        };
}
