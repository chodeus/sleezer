using System.IO;
using System.Text.Json;
using NLog;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;

/// <summary>A grab's tagged and renamed files, a local path and size each.</summary>
public sealed record SlskdTaggedFile(string Path, long Size, bool Renamed);

/// <summary>Keeps each grab's tagged and renamed files on disk, so a Lidarr restart doesn't lose which files are the grab's.</summary>
public sealed class SlskdTaggedFilesStore(string folder, Logger logger)
{
    public void Save(string downloadId, IReadOnlyCollection<SlskdTaggedFile> files)
    {
        if (PathFor(downloadId) is not { } path)
            return;

        try
        {
            Directory.CreateDirectory(folder);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(files));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            // Unsaved: a restart forgets these files and removal keeps them, as before.
            logger.Debug(ex, "Could not save the tagged files of {DownloadId}", downloadId);
        }
    }

    public IReadOnlyList<SlskdTaggedFile> Load(string downloadId)
    {
        if (PathFor(downloadId) is not { } path || !File.Exists(path))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<SlskdTaggedFile>>(File.ReadAllText(path)) ?? [];
        }
        catch (Exception ex)
        {
            // Unreadable: no file is claimed from it, so removal keeps them.
            logger.Debug(ex, "Could not read the tagged files of {DownloadId}", downloadId);
            return [];
        }
    }

    public void Delete(string downloadId)
    {
        if (PathFor(downloadId) is not { } path)
            return;

        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            logger.Debug(ex, "Could not delete the tagged files of {DownloadId}", downloadId);
        }
    }

    // The download id names the file; anything beyond letters, digits and dashes could point it elsewhere.
    private string? PathFor(string downloadId) =>
        downloadId.Length > 0 && downloadId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
            ? System.IO.Path.Combine(folder, downloadId + ".json")
            : null;
}
