using System.Collections.Concurrent;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Plugin.Sleezer.Core.Utilities;

namespace NzbDrone.Plugin.Sleezer.Core.PostProcessing;

/// <summary>Downloads held back from automatic import, and why.</summary>
public interface IImportHolds
{
    void Hold(string downloadId, string reason);

    string? ReasonFor(string downloadId);

    void Release(string downloadId);
}

/// <summary>Keeps each hold on disk, so a Lidarr restart doesn't import a held download.</summary>
public sealed class ImportHolds : IImportHolds
{
    private readonly ConcurrentDictionary<string, string> _held = new(StringComparer.Ordinal);
    private readonly string _folder;
    private readonly Logger _logger;

    public ImportHolds(IAppFolderInfo appFolderInfo, Logger logger)
    {
        _folder = Path.Combine(PluginFolder.Resolve(appFolderInfo.GetPluginPath(), PluginInfo.RepoUrl), "import-holds");
        _logger = logger;
    }

    /// <summary>Why a tagged download should wait for a manual import, or null when it shouldn't.</summary>
    // Only this download's file counts: a shared folder holds other downloads' files too.
    public static string? ReasonToHold(PreImportTagger.TaggingResult result, bool verifying, IReadOnlySet<string> ownedPaths) =>
        verifying && result.WrongRecording is { } reason && result.WrongRecordingFile is { } file && ownedPaths.Contains(file) ? reason : null;

    public void Hold(string downloadId, string reason)
    {
        _held[downloadId] = reason;
        if (PathFor(downloadId) is not { } path)
            return;

        try
        {
            DownloadIdFile.Write(path, reason);
        }
        catch (Exception ex)
        {
            // Unsaved: held until Lidarr restarts, then imported as before this hold existed.
            _logger.Warn(ex, "Could not save the import hold of {DownloadId}; a Lidarr restart would let it import", downloadId);
        }
    }

    public string? ReasonFor(string downloadId)
    {
        if (_held.TryGetValue(downloadId, out string? held))
            return held;
        if (PathFor(downloadId) is not { } path)
            return null;

        // Not File.Exists: it answers false for a folder that can't be listed, and that hold must still hold.
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex)
        {
            // A hold that exists but can't be read still holds.
            _logger.Warn(ex, "Could not read the import hold of {DownloadId}; keeping it held", downloadId);
            return "the hold's reason could not be read";
        }
    }

    public void Release(string downloadId)
    {
        _held.TryRemove(downloadId, out _);
        if (PathFor(downloadId) is not { } path)
            return;

        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Could not delete the import hold of {DownloadId}", downloadId);
        }
    }

    private string? PathFor(string downloadId) => DownloadIdFile.PathFor(_folder, downloadId, ".txt");
}
