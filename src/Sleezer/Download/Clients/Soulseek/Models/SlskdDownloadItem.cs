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

    // A multi-disc release spans several remote directories (Album\CD1,
    // Album\CD2); slskd reports transfers per remote directory, so the item
    // aggregates them and exposes a merged view through SlskdDownloadDirectory.
    private readonly Dictionary<string, SlskdDownloadDirectory> _remoteDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _enqueuedFilenames;
    private readonly Dictionary<string, SlskdFileState> _previousFileStates = [];

    public List<Task> PostProcessTasks { get; } = [];
    public DownloadItemStatus? LastReportedStatus { get; set; }
    public bool DiscFoldersMerged { get; set; }
    public IReadOnlyDictionary<string, SlskdFileState> FileStates => _previousFileStates;

    public SlskdDownloadDirectory? SlskdDownloadDirectory
    {
        get => BuildMergedDirectory();
        set => AddOrUpdateDirectory(value);
    }

    public SlskdDownloadItem(ReleaseInfo releaseInfo)
    {
        _logger = NzbDroneLogger.GetLogger(this);
        ReleaseInfo = releaseInfo;
        FileData = JsonSerializer.Deserialize<List<SlskdFileData>>(ReleaseInfo.Source, _jsonOptions) ?? [];
        ID = GetStableMD5Id(FileData.Select(file => file.Filename));
        _enqueuedFilenames = FileData
            .Select(f => f.Filename)
            .Where(f => !string.IsNullOrEmpty(f))
            .Select(f => f!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _logger.Trace("Created SlskdDownloadItem with ID: {Id}", ID);
    }

    /// <summary>True when this item enqueued the given remote file.</summary>
    public bool OwnsFile(string? remoteFilename) =>
        !string.IsNullOrEmpty(remoteFilename) && _enqueuedFilenames.Contains(remoteFilename);

    /// <summary>True when this item tracks transfers for the given remote directory.</summary>
    public bool TracksRemoteDirectory(string? remoteDirectory) =>
        !string.IsNullOrEmpty(remoteDirectory) && _remoteDirectories.ContainsKey(remoteDirectory);

    /// <summary>
    /// True when the enqueued files span more than one remote directory
    /// (multi-disc share with CD1/CD2 subfolders).
    /// </summary>
    public bool IsMultiDirectory => RemoteDirectoryLeaves().Count > 1;

    /// <summary>
    /// Leaf folder names of every remote directory the enqueued files live in
    /// — these are the local folder names slskd downloads into.
    /// </summary>
    public IReadOnlyCollection<string> RemoteDirectoryLeaves()
    {
        HashSet<string> leaves = new(StringComparer.OrdinalIgnoreCase);
        foreach (SlskdFileData file in FileData)
        {
            string dir = SlskdTextProcessor.GetDirectoryFromFilename(file.Filename);
            string leaf = LeafOf(dir);
            if (leaf.Length > 0)
                leaves.Add(leaf);
        }

        return leaves;
    }

    /// <summary>
    /// Local album folder name for a multi-disc item: the leaf of the common
    /// remote parent directory (disc subfolders folded away). Falls back to the
    /// item ID when the share layout gives no usable name.
    /// </summary>
    public string LocalAlbumFolderName()
    {
        HashSet<string> mergedKeys = new(StringComparer.OrdinalIgnoreCase);
        foreach (SlskdFileData file in FileData)
        {
            string key = SlskdTextProcessor.GetMergedDirectoryKey(file.Filename);
            if (!string.IsNullOrEmpty(key))
                mergedKeys.Add(key);
        }

        string leaf = mergedKeys.Count == 1 ? LeafOf(mergedKeys.First()) : "";
        return leaf is "" or "." or ".." ? ID : leaf;
    }

    private static string LeafOf(string path)
    {
        string trimmed = path.Replace('\\', '/').TrimEnd('/');
        int idx = trimmed.LastIndexOf('/');
        string leaf = idx >= 0 ? trimmed[(idx + 1)..] : trimmed;
        return leaf is "." or ".." ? "" : leaf;
    }

    private void AddOrUpdateDirectory(SlskdDownloadDirectory? directory)
    {
        if (directory == null || string.IsNullOrEmpty(directory.Directory))
            return;

        if (_remoteDirectories.TryGetValue(directory.Directory, out SlskdDownloadDirectory? existing) && existing == directory)
            return;

        CompareFileStates(directory);
        _remoteDirectories[directory.Directory] = directory;
    }

    private SlskdDownloadDirectory? BuildMergedDirectory()
    {
        if (_remoteDirectories.Count == 0)
            return null;

        if (_remoteDirectories.Count == 1)
            return _remoteDirectories.Values.First();

        List<SlskdDownloadFile> allFiles = _remoteDirectories.Values
            .Where(d => d.Files != null)
            .SelectMany(d => d.Files!)
            .ToList();

        // Fold each directory's disc-leaf away; when they all share one parent
        // (the normal Album\CD1 + Album\CD2 case) report that parent.
        List<string> mergedParents = _remoteDirectories.Keys
            .Select(key => SlskdTextProcessor.GetMergedDirectoryKey(key + "\\x"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        string commonParent = mergedParents.Count == 1 ? mergedParents[0] : _remoteDirectories.Keys.First();

        return new SlskdDownloadDirectory(commonParent, allFiles.Count, allFiles);
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

    public OsPath GetFullFolderPath(OsPath downloadPath)
    {
        // Multi-disc: slskd downloads each remote disc directory into its own
        // local folder; the manager merges those into one album folder after
        // completion, and that folder is what Lidarr imports.
        if (IsMultiDirectory)
            return new OsPath(Path.Combine(downloadPath.FullPath, LocalAlbumFolderName()));

        string leaf = SlskdDownloadDirectory?.Directory
            .Replace('\\', '/')
            .TrimEnd('/')
            .Split('/')
            .LastOrDefault() ?? "";

        // A peer-advertised directory like "album/.." reduces to a ".." leaf and
        // Path.Combine(root, "..") escapes the download root. Refuse relative
        // segments — fall back to the bare root, which the delete guards reject.
        if (leaf is ".." or ".")
            leaf = "";

        return new OsPath(Path.Combine(downloadPath.FullPath, leaf));
    }
}
