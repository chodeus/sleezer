using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.Download;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;
using System.Text.Json;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;

public class SlskdDownloadItem
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly Logger _logger;

    public string ID { get; set; }
    public List<SlskdFileData> FileData { get; set; } = [];
    public string? Username { get; set; }
    public ReleaseInfo ReleaseInfo { get; set; }

    /// <summary>
    /// Fully resolved album from Lidarr's RemoteAlbum, attached at grab time so
    /// downstream post-processing (corruption scan, pre-import tagging) can resolve
    /// Artist/Tracks without re-querying. Null when the item was reconstructed from
    /// download history in inclusive mode.
    /// </summary>
    public Album? ResolvedAlbum { get; set; }

    public event EventHandler<SlskdFileState>? FileStateChanged;

    private SlskdDownloadDirectory? _slskdDownloadDirectory;
    private readonly Dictionary<string, SlskdFileState> _previousFileStates = [];

    public List<Task> PostProcessTasks { get; } = [];
    public DownloadItemStatus? LastReportedStatus { get; set; }
    public IReadOnlyDictionary<string, SlskdFileState> FileStates => _previousFileStates;

    public SlskdDownloadDirectory? SlskdDownloadDirectory
    {
        get => _slskdDownloadDirectory;
        set
        {
            if (_slskdDownloadDirectory == value)
                return;
            CompareFileStates(value);
            _slskdDownloadDirectory = value;
        }
    }

    public SlskdDownloadItem(ReleaseInfo releaseInfo)
    {
        _logger = NzbDroneLogger.GetLogger(this);
        ReleaseInfo = releaseInfo;
        FileData = JsonSerializer.Deserialize<List<SlskdFileData>>(ReleaseInfo.Source, _jsonOptions) ?? [];
        ID = GetStableMD5Id(FileData.Select(file => file.Filename));
        _logger.Trace("Created SlskdDownloadItem with ID: {Id}", ID);
    }

    public static string GetStableMD5Id(IEnumerable<string?> filenames)
    {
        string combined = string.Join("|", filenames.Order());
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(combined);
        return BitConverter.ToString(System.Security.Cryptography.MD5.HashData(bytes)).Replace("-", "").ToLowerInvariant();
    }

    private void CompareFileStates(SlskdDownloadDirectory? newDirectory)
    {
        if (newDirectory?.Files == null)
            return;

        foreach (SlskdDownloadFile file in newDirectory.Files)
        {
            if (_previousFileStates.TryGetValue(file.Filename, out SlskdFileState? fileState) && fileState != null)
            {
                string previousState = fileState.State;
                fileState.UpdateFile(file);
                if (fileState.State != previousState)
                {
                    _logger.Trace("State change: {Filename} | {Previous} -> {Next}", Path.GetFileName(file.Filename), previousState, fileState.State);
                    FileStateChanged?.Invoke(this, fileState);
                }
            }
            else
            {
                SlskdFileState newFileState = new(file);
                _previousFileStates.Add(file.Filename, newFileState);

                DownloadItemStatus initialStatus = SlskdFileState.GetStatus(file.State);
                if (initialStatus == DownloadItemStatus.Failed)
                {
                    _logger.Trace("Initial state is failed: {Filename} | {State}", Path.GetFileName(file.Filename), file.State);
                    FileStateChanged?.Invoke(this, newFileState);
                }
            }
        }
    }

    public OsPath GetFullFolderPath(OsPath downloadPath) => new(Path.Combine(
        downloadPath.FullPath,
        SlskdDownloadDirectory?.Directory
            .Replace('\\', '/')
            .TrimEnd('/')
            .Split('/')
            .LastOrDefault() ?? ""));
}
