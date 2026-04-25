using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;
using System.Collections.Concurrent;
using System.Text.Json;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Music;
using NzbDrone.Plugin.Sleezer.Core.Model;
using NzbDrone.Plugin.Sleezer.Core.PostProcessing;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek;
using NzbDrone.Plugin.Sleezer.Metadata.FFmpeg;
using XabeFFmpeg = Xabe.FFmpeg.FFmpeg;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;

internal static class SlskdEventTypes
{
    public const string DownloadDirectoryComplete = "DownloadDirectoryComplete";
    public const string DownloadFileComplete = "DownloadFileComplete";
}

public class SlskdDownloadManager : ISlskdDownloadManager
{
    private readonly ConcurrentDictionary<DownloadKey<int, string>, SlskdDownloadItem> _downloadMappings = new();

    // Adaptive transfer poll times per definition ID
    private readonly ConcurrentDictionary<int, DateTime> _lastTransferPollTimes = new();
    // Event poll times per definition ID (separate from transfer poll)
    private readonly ConcurrentDictionary<int, DateTime> _lastEventPollTimes = new();
    // Last-seen event offset per definition ID for incremental polling
    private readonly ConcurrentDictionary<int, int> _lastEventOffsets = new();
    // Latest settings snapshot per definition ID: used by event-triggered retry callbacks
    private readonly ConcurrentDictionary<int, SlskdProviderSettings> _settingsCache = new();

    private readonly ISlskdApiClient _apiClient;
    private readonly IDownloadHistoryService _downloadHistoryService;
    private readonly ISlskdItemsParser _slskdItemsParser;
    private readonly IRemotePathMappingService _remotePathMappingService;
    private readonly IDiskProvider _diskProvider;
    private readonly Logger _logger;
    private readonly SlskdRetryHandler _retryHandler;
    private readonly ICorruptionScanner _corruptionScanner;
    private readonly IPreImportTagger _preImportTagger;
    private readonly ISlskdCorruptionHandler _corruptionHandler;
    private readonly IMetadataFactory _metadataFactory;
    private readonly ISlskdWatchdog _watchdog;
    private bool _ffmpegResolved;

    /// <summary>
    /// Tracks per-item post-processing completion so the owning item's status
    /// can't flip to Completed until the scan + tag pass has finished.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _postProcessed = new();

    public SlskdDownloadManager(
        ISlskdApiClient apiClient,
        IDownloadHistoryService downloadHistoryService,
        ISlskdItemsParser slskdItemsParser,
        IRemotePathMappingService remotePathMappingService,
        IDiskProvider diskProvider,
        ICorruptionScanner corruptionScanner,
        IPreImportTagger preImportTagger,
        ISlskdCorruptionHandler corruptionHandler,
        IMetadataFactory metadataFactory,
        ISlskdWatchdog watchdog,
        Logger logger)
    {
        _apiClient = apiClient;
        _downloadHistoryService = downloadHistoryService;
        _slskdItemsParser = slskdItemsParser;
        _remotePathMappingService = remotePathMappingService;
        _diskProvider = diskProvider;
        _logger = logger;
        _corruptionScanner = corruptionScanner;
        _preImportTagger = preImportTagger;
        _corruptionHandler = corruptionHandler;
        _metadataFactory = metadataFactory;
        _watchdog = watchdog;
        _retryHandler = new SlskdRetryHandler(apiClient, NzbDroneLogger.GetLogger(typeof(SlskdRetryHandler)));
    }

    /// <summary>
    /// Resolves ffmpeg from the configured FFmpeg metadata provider, mirroring
    /// the path it validated against. Runs once per process lifetime so we
    /// don't re-read settings on every scan. The FFmpeg metadata provider is
    /// NzbDrone.Plugin.Sleezer's canonical ffmpeg configurator — this keeps the
    /// corruption scanner from needing a duplicate FFmpeg Path setting on the
    /// slskd client.
    /// </summary>
    private void EnsureFFmpegResolved()
    {
        if (_ffmpegResolved)
            return;

        try
        {
            FFmpegSettings? ffmpegSettings = GetSharedPostProcessingSettings();

            if (ffmpegSettings != null && !string.IsNullOrWhiteSpace(ffmpegSettings.FFmpegPath))
            {
                XabeFFmpeg.SetExecutablesPath(ffmpegSettings.FFmpegPath);
                AudioMetadataHandler.ResetFFmpegInstallationCheck();
                _logger.Trace($"[post-process] Applied FFmpeg path: {ffmpegSettings.FFmpegPath}");
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[post-process] Failed to resolve ffmpeg from FFmpeg metadata settings; falling back to PATH lookup.");
        }

        _ffmpegResolved = true;
    }

    private FFmpegSettings? GetSharedPostProcessingSettings()
    {
        try
        {
            return _metadataFactory.All()
                .Where(d => d.Settings is FFmpegSettings)
                .Select(d => d.Settings as FFmpegSettings)
                .FirstOrDefault(s => s != null);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "[post-process] Failed to read shared post-processing settings; treating toggles as disabled.");
            return null;
        }
    }

    public async Task<string> DownloadAsync(RemoteAlbum remoteAlbum, int definitionId, SlskdProviderSettings settings)
    {
        _settingsCache[definitionId] = settings;

        SlskdDownloadItem item = new(remoteAlbum.Release)
        {
            ResolvedAlbum = remoteAlbum.Albums?.FirstOrDefault()
        };
        _logger.Trace($"Download initiated: {remoteAlbum.Release.Title} | Files: {item.FileData.Count}");

        try
        {
            string username = ExtractUsernameFromPath(remoteAlbum.Release.DownloadUrl);
            List<(string Filename, long Size)> files = ParseFilesFromSource(remoteAlbum.Release.Source);

            await _apiClient.EnqueueDownloadAsync(settings, username, files);
            item.Username = username;
            SubscribeStateChanges(item, definitionId);
            AddItem(definitionId, item);

            return item.ID;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"Download enqueue failed for {remoteAlbum.Release.Title}");
            throw;
        }
    }

    public IEnumerable<DownloadClientItem> GetItems(int definitionId, SlskdProviderSettings settings, OsPath remotePath)
    {
        _settingsCache[definitionId] = settings;

        try
        {
            RefreshAsync(definitionId, settings).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to update download items from Slskd. Returning cached items.");
        }

        TimeSpan? timeout = settings.GetTimeout();
        DateTime now = DateTime.UtcNow;

        foreach (SlskdDownloadItem item in GetItemsForDef(definitionId))
        {
            DownloadClientItem clientItem;
            try
            {
                SlskdStatusResolver.DownloadStatus resolved = SlskdStatusResolver.Resolve(item, timeout, now);
                clientItem = new()
                {
                    DownloadId = item.ID,
                    Title = item.ReleaseInfo.Title,
                    CanBeRemoved = true,
                    CanMoveFiles = true,
                    OutputPath = item.GetFullFolderPath(remotePath),
                    Status = resolved.Status,
                    Message = resolved.Message,
                    TotalSize = resolved.TotalSize,
                    RemainingSize = resolved.RemainingSize,
                    RemainingTime = resolved.RemainingTime,
                };

                EmitCompletionSpan(item, resolved);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, $"Failed to build DownloadClientItem for {item.ID}. Skipping.");
                continue;
            }

            yield return clientItem;
        }
    }

    public void RemoveItem(DownloadClientItem clientItem, bool deleteData, int definitionId, SlskdProviderSettings settings)
    {
        if (!deleteData)
            return;

        SlskdDownloadItem? item = GetItem(definitionId, clientItem.DownloadId);
        if (item == null)
            return;

        string? directory = item.SlskdDownloadDirectory?.Directory;

        // Run synchronously so Lidarr sees failures in its own log rather than
        // silently fire-and-forgetting the work behind its back.
        try
        {
            RemoveItemFilesAsync(item, settings).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.Warn(ex, $"[def={definitionId}] Failed to remove slskd transfers / files for {clientItem.Title}");
        }

        TryDeleteOutputFolder(clientItem, settings);

        RemoveItemFromDict(definitionId, clientItem.DownloadId);

        if (settings.CleanStaleDirectories && !string.IsNullOrEmpty(directory))
            _ = CleanStaleDirectoriesAsync(directory, settings);
    }

    private void TryDeleteOutputFolder(DownloadClientItem clientItem, SlskdProviderSettings settings)
    {
        if (clientItem.OutputPath.IsEmpty)
        {
            _logger.Debug($"[{clientItem.Title}] No OutputPath recorded, skipping folder deletion");
            return;
        }

        string localRoot = _remotePathMappingService
            .RemapRemoteToLocal(settings.Host, new OsPath(settings.DownloadPath))
            .FullPath
            .TrimEnd('/', '\\');
        string folder = clientItem.OutputPath.FullPath.TrimEnd('/', '\\');

        // Safety: if OutputPath collapses to the remapped download root (happens
        // when the item was never populated with a SlskdDownloadDirectory, so
        // GetFullFolderPath returned the bare root), refuse - we'd wipe every
        // other download.
        if (string.Equals(folder, localRoot, StringComparison.OrdinalIgnoreCase))
        {
            _logger.Warn($"[{clientItem.Title}] OutputPath resolves to the download root, refusing to delete");
            return;
        }

        try
        {
            if (_diskProvider.FolderExists(folder))
            {
                _logger.Debug($"[{clientItem.Title}] Deleting folder '{folder}'");
                _diskProvider.DeleteFolder(folder, true);
            }
            else if (_diskProvider.FileExists(folder))
            {
                _logger.Debug($"[{clientItem.Title}] Deleting file '{folder}'");
                _diskProvider.DeleteFile(folder);
            }
            else
            {
                _logger.Debug($"[{clientItem.Title}] Folder '{folder}' already gone, skipping");
            }
        }
        catch (Exception ex)
        {
            _logger.Warn(ex, $"[{clientItem.Title}] Failed to delete output folder '{folder}'");
        }
    }

    private async Task RefreshAsync(int definitionId, SlskdProviderSettings settings)
    {
        HashSet<string> activeUsernames = GetActiveUsernames(definitionId);

        TimeSpan transferInterval = activeUsernames.Count > 0 ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(30);

        DateTime now = DateTime.UtcNow;

        DateTime lastTransfer = _lastTransferPollTimes.GetOrAdd(definitionId, DateTime.MinValue);
        if (now - lastTransfer >= transferInterval)
        {
            await PollTransfersAsync(definitionId, settings, activeUsernames);
            _lastTransferPollTimes[definitionId] = DateTime.UtcNow;
        }

        DateTime lastEvent = _lastEventPollTimes.GetOrAdd(definitionId, DateTime.MinValue);
        if (now - lastEvent >= TimeSpan.FromSeconds(5))
        {
            int offset = _lastEventOffsets.GetOrAdd(definitionId, 0);
            await PollEventsAsync(definitionId, settings, offset);
            _lastEventPollTimes[definitionId] = DateTime.UtcNow;
        }
    }

    private async Task PollTransfersAsync(int definitionId, SlskdProviderSettings settings, HashSet<string> activeUsernames)
    {
        ConcurrentDictionary<string, bool> currentIdSet = new();

        if (!settings.Inclusive && activeUsernames.Count > 0)
        {
            await Task.WhenAll(activeUsernames.Select(async username =>
            {
                SlskdUserTransfers? userTransfers = await _apiClient.GetUserTransfersAsync(settings, username);
                if (userTransfers != null)
                    ProcessUserTransfers(definitionId, settings, userTransfers, currentIdSet);
            }));
        }
        else
        {
            List<SlskdUserTransfers> all = await _apiClient.GetAllTransfersAsync(settings);
            foreach (SlskdUserTransfers user in all)
                ProcessUserTransfers(definitionId, settings, user, currentIdSet);
        }

        _logger.Debug($"[def={definitionId}] Polled {activeUsernames.Count} users | Tracked: {currentIdSet.Count}");

        // Run watchdog on all tracked items - states are freshly updated above.
        // Fire-and-forget per item with per-item error isolation so one peer's
        // slow DELETE doesn't block the others.
        foreach (SlskdDownloadItem item in GetItemsForDef(definitionId).ToList())
        {
            try
            {
                await _watchdog.InspectAsync(item, settings, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, $"[def={definitionId}] Watchdog failed for item {item.ID}");
            }
        }

        if (settings.Inclusive)
        {
            foreach (SlskdDownloadItem item in GetItemsForDef(definitionId)
                .Where(i => !currentIdSet.ContainsKey(i.ID) && i.ReleaseInfo.DownloadProtocol == null)
                .ToList())
            {
                _logger.Trace($"[def={definitionId}] Pruning inclusive item {item.ID} (gone from Slskd)");
                RemoveItemFromDict(definitionId, item.ID);
            }
        }
    }

    private void ProcessUserTransfers(
        int definitionId,
        SlskdProviderSettings settings,
        SlskdUserTransfers userTransfers,
        ConcurrentDictionary<string, bool> currentIdSet)
    {
        foreach (SlskdDownloadDirectory dir in userTransfers.Directories)
        {
            string hash = SlskdDownloadItem.GetStableMD5Id(dir.Files?.Select(f => f.Filename) ?? []);
            currentIdSet.TryAdd(hash, true);

            SlskdDownloadItem? item = GetItem(definitionId, hash);
            if (item == null)
            {
                _logger.Trace($"[def={definitionId}] Unknown item {hash}: checking history");
                DownloadHistory? history = _downloadHistoryService.GetLatestGrab(hash);

                if (history != null)
                    item = new SlskdDownloadItem(history.Release);
                else if (settings.Inclusive)
                    item = new SlskdDownloadItem(CreateReleaseInfoFromDirectory(userTransfers.Username, dir));

                if (item == null)
                    continue;

                SubscribeStateChanges(item, definitionId);
                AddItem(definitionId, item);
            }

            item.Username ??= userTransfers.Username;
            item.SlskdDownloadDirectory = dir;

            // Fallback trigger for post-process: if the transfer poll sees all
            // files completed before the event poll has caught up with the matching
            // DownloadDirectoryComplete event, enqueue here. Without this, Lidarr
            // can see status=Completed and start importing before the scan runs.
            // _postProcessed.TryAdd dedupes against the event-path trigger.
            if (AllFilesCompleted(item))
                EnqueuePostProcess(item, settings);
        }
    }

    private static bool AllFilesCompleted(SlskdDownloadItem item)
    {
        IReadOnlyDictionary<string, SlskdFileState> states = item.FileStates;
        if (states.Count == 0)
            return false;

        foreach (SlskdFileState state in states.Values)
            if (state.GetStatus() != DownloadItemStatus.Completed)
                return false;

        return true;
    }

    private async Task PollEventsAsync(int definitionId, SlskdProviderSettings settings, int offset)
    {
        (List<SlskdEventRecord> events, _) = await _apiClient.GetEventsAsync(settings, offset, 50);
        if (events.Count == 0)
            return;

        foreach (SlskdEventRecord record in events)
        {
            try
            {
                await HandleEventAsync(definitionId, settings, record);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, $"[def={definitionId}] Failed to process event {record.Type} ({record.Id})");
            }
        }

        _lastEventOffsets[definitionId] = offset + events.Count;
    }

    private async Task HandleEventAsync(int definitionId, SlskdProviderSettings settings, SlskdEventRecord record)
    {
        if (string.IsNullOrEmpty(record.Data))
            return;

        if (record.Type == SlskdEventTypes.DownloadDirectoryComplete)
        {
            using JsonDocument doc = JsonDocument.Parse(record.Data);
            string remoteDir = doc.RootElement.TryGetProperty("remoteDirectoryName", out JsonElement rdn) ? rdn.GetString() ?? "" : "";
            string username = doc.RootElement.TryGetProperty("username", out JsonElement un) ? un.GetString() ?? "" : "";

            _logger.Trace($"[def={definitionId}] Event DownloadDirectoryComplete: {remoteDir} by {username}: forcing refresh");

            // Refresh transfer state FIRST so any item whose SlskdDownloadDirectory
            // hasn't been populated yet (common for downloads that complete between
            // 30s poll cycles) gets its directory filled in before we look it up.
            SlskdUserTransfers? userTransfers = await _apiClient.GetUserTransfersAsync(settings, username);
            if (userTransfers != null)
                ProcessUserTransfers(definitionId, settings, userTransfers, new ConcurrentDictionary<string, bool>());

            SlskdDownloadItem? item = GetItemsForDef(definitionId)
                .FirstOrDefault(i => i.Username == username &&
                                     string.Equals(i.SlskdDownloadDirectory?.Directory, remoteDir, StringComparison.OrdinalIgnoreCase));

            if (item != null)
            {
                EnqueuePostProcess(item, settings);
            }
            else if (userTransfers == null)
            {
                // slskd has no active transfers for this user — the event log is
                // replaying an old completion whose transfer was already cleaned
                // up. Nothing to post-process; stay quiet.
                _logger.Trace($"[def={definitionId}] DownloadDirectoryComplete for {remoteDir} by {username}: stale replayed event, no active transfer at slskd — skipping");
            }
            else
            {
                _logger.Warn($"[def={definitionId}] DownloadDirectoryComplete for {remoteDir} by {username} but no tracked item matched — skipping post-process");
            }
        }
        else if (record.Type == SlskdEventTypes.DownloadFileComplete && _logger.IsTraceEnabled)
        {
            using JsonDocument doc = JsonDocument.Parse(record.Data);
            if (doc.RootElement.TryGetProperty("transfer", out JsonElement transferEl))
            {
                string filename = transferEl.TryGetProperty("filename", out JsonElement fn) ? fn.GetString() ?? "" : "";
                string username = transferEl.TryGetProperty("username", out JsonElement un) ? un.GetString() ?? "" : "";
                _logger.Trace($"[def={definitionId}] Event DownloadFileComplete: {Path.GetFileName(filename)} by {username}");
            }
        }
    }

    // Per-file ffmpeg decode timeout. Hardcoded: 120s handles any real track;
    // corrupt files that hang ffmpeg get killed at this limit.
    private const int CorruptionScanTimeoutSeconds = 120;

    // Confidence threshold for pre-import tagging. 0.15 is stricter than
    // Lidarr's importer (~0.25) since a mis-tag is more permanent than
    // a skip-tag.
    private const double TagConfidenceThreshold = 0.15;

    private void EnqueuePostProcess(SlskdDownloadItem item, SlskdProviderSettings settings)
    {
        // Guard against duplicate post-process tasks: slskd can emit both
        // DownloadDirectoryComplete and a synthetic refresh-triggered completion.
        if (!_postProcessed.TryAdd(item.ID, 0))
            return;

        FFmpegSettings? sharedSettings = GetSharedPostProcessingSettings();
        bool scanEnabled = (sharedSettings?.EnableCorruptFileScan ?? false) && (sharedSettings?.CorruptionCheckSlskd ?? false);
        bool tagEnabled = (sharedSettings?.EnablePreImportTagging ?? false) && (sharedSettings?.PreImportTaggingSlskd ?? false);

        if (!scanEnabled && !tagEnabled)
            return;

        Task task = Task.Run(async () =>
        {
            using CancellationTokenSource cts = new(TimeSpan.FromMinutes(30));

            try
            {
                // Sanity delay: slskd reports DownloadDirectoryComplete before it
                // finishes moving files out of the incomplete dir. Give it a moment.
                await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);

                OsPath remotePath = GetRemoteDownloadPath(settings);
                string folderPath = item.GetFullFolderPath(remotePath).FullPath;

                if (!_diskProvider.FolderExists(folderPath))
                {
                    _logger.Warn($"[post-process] Folder missing after DownloadDirectoryComplete: {folderPath}");
                    return;
                }

                if (scanEnabled)
                {
                    // 1. Corruption scan across every audio file in the folder.
                    List<SlskdCorruptionStrike> strikes = await ScanFolderForCorruptionAsync(item, folderPath, cts.Token);

                    if (strikes.Count > 0)
                    {
                        // 2a. Corruption path: delete folder, short-circuit retries,
                        //     publish DownloadFailedEvent. Skip tagging (file is gone).
                        await _corruptionHandler.HandleCorruptDownloadAsync(item, strikes, folderPath, settings, cts.Token);
                        return;
                    }
                }

                if (tagEnabled)
                {
                    Album? album = item.ResolvedAlbum;
                    Artist? artist = album?.Artist?.Value;
                    if (album == null || artist == null)
                    {
                        _logger.Debug($"[post-process] Pre-import tag: skipping {item.ID}; no ResolvedAlbum/Artist (item likely reconstructed from history in inclusive mode).");
                        return;
                    }

                    AlbumRelease? albumRelease = album.AlbumReleases?.Value?.FirstOrDefault(r => r.Monitored)
                                                  ?? album.AlbumReleases?.Value?.FirstOrDefault();

                    // 2b. Clean path: run Lidarr-backed pre-import tagging.
                    await _preImportTagger.TagCompletedDownloadAsync(
                        album,
                        artist,
                        albumRelease,
                        item.ID,
                        folderPath,
                        TagConfidenceThreshold,
                        sharedSettings?.StripFeaturedArtists ?? false,
                        cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.Warn($"[post-process] Timed out for {item.ID}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"[post-process] Unexpected failure for {item.ID}");
            }
        });

        item.PostProcessTasks.Add(task);
    }

    private async Task<List<SlskdCorruptionStrike>> ScanFolderForCorruptionAsync(
        SlskdDownloadItem item,
        string folderPath,
        CancellationToken ct)
    {
        List<SlskdCorruptionStrike> strikes = new();

        // Re-apply the FFmpeg metadata provider's path if set (Xabe's global state
        // resets on Lidarr restart; the provider's Test only writes it for the current run).
        EnsureFFmpegResolved();

        // Only scan audio files. Non-audio artifacts (covers, nfos, logs) would
        // fail TagLib parsing and incorrectly flag the whole album as corrupt.
        string[] audioFiles = _diskProvider.GetFiles(folderPath, recursive: true)
            .Where(IsAudioExtension)
            .ToArray();
        if (audioFiles.Length == 0)
        {
            _logger.Info($"[post-process] {item.ID}: no audio files in {folderPath}; nothing to scan");
            return strikes;
        }

        _logger.Info($"[post-process] {item.ID}: scanning {audioFiles.Length} audio file(s) in {folderPath}");
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

        // Concurrency tuned to machine. Ffmpeg is CPU-bound so we cap at half
        // the core count to leave headroom for the rest of Lidarr.
        int concurrency = Math.Max(2, Environment.ProcessorCount / 2);
        using SemaphoreSlim gate = new(concurrency);

        Task<(string path, CorruptionScanner.Result result)>[] tasks = audioFiles.Select(async path =>
        {
            await gate.WaitAsync(ct);
            try
            {
                CorruptionScanner.Result r = await _corruptionScanner.ScanAsync(path, CorruptionScanTimeoutSeconds, ct);
                return (path, r);
            }
            finally { gate.Release(); }
        }).ToArray();

        foreach (Task<(string path, CorruptionScanner.Result result)> t in tasks)
        {
            (string path, CorruptionScanner.Result result) = await t;
            if (!result.IsCorrupt)
                continue;

            // Map disk path back to the SlskdFileState for retry short-circuit.
            string filename = Path.GetFileName(path);
            SlskdFileState? fileState = item.FileStates.Values
                .FirstOrDefault(fs => string.Equals(Path.GetFileName(fs.File.Filename), filename, StringComparison.OrdinalIgnoreCase));

            if (fileState == null)
                _logger.Warn($"[post-process] Corrupt file {filename} not mapped to any tracked slskd transfer; counting as corruption but skipping retry short-circuit.");

            strikes.Add(new SlskdCorruptionStrike(fileState, result.Reason));
        }

        watch.Stop();
        _logger.Info($"[post-process] {item.ID}: scan complete in {watch.Elapsed.TotalSeconds:F1}s \u2014 {audioFiles.Length} file(s), {strikes.Count} corrupt");

        return strikes;
    }

    private OsPath GetRemoteDownloadPath(SlskdProviderSettings settings)
    {
        OsPath remote = new(settings.DownloadPath);
        return _remotePathMappingService.RemapRemoteToLocal(settings.Host, remote);
    }

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".mp3", ".m4a", ".ogg", ".opus", ".wav",
        ".wma", ".aac", ".aiff", ".aif", ".ape", ".wv",
        ".alac", ".m4b", ".m4p", ".mp2", ".mpc", ".dsf", ".dff"
    };

    private static bool IsAudioExtension(string path) =>
        AudioExtensions.Contains(Path.GetExtension(path));

    private void EmitCompletionSpan(SlskdDownloadItem item, SlskdStatusResolver.DownloadStatus resolved)
    {
        bool isTerminal = resolved.Status is DownloadItemStatus.Completed or DownloadItemStatus.Failed;
        if (!isTerminal || item.LastReportedStatus == resolved.Status)
            return;

        item.LastReportedStatus = resolved.Status;
    }

    private void SubscribeStateChanges(SlskdDownloadItem item, int definitionId)
    {
        item.FileStateChanged += (sender, fileState) =>
        {
            if (_settingsCache.TryGetValue(definitionId, out SlskdProviderSettings? s))
                _retryHandler.OnFileStateChanged(sender as SlskdDownloadItem, fileState, s);
        };
    }

    private async Task RemoveItemFilesAsync(SlskdDownloadItem item, SlskdProviderSettings settings)
    {
        List<SlskdDownloadFile> files = item.SlskdDownloadDirectory?.Files ?? [];
        if (files.Count == 0 || item.Username == null)
        {
            _logger.Debug($"No slskd transfers to cancel for {item.ID} (directory not populated); relying on local folder deletion");
            return;
        }

        await Task.WhenAll(files.Select(async file =>
        {
            if (SlskdFileState.GetStatus(file.State) != DownloadItemStatus.Completed)
            {
                await _apiClient.DeleteTransferAsync(settings, item.Username, file.Id);
                await Task.Delay(1000);
            }
            await _apiClient.DeleteTransferAsync(settings, item.Username, file.Id, remove: true);

            try
            {
                string relativePath = file.Filename;
                if (relativePath.StartsWith(settings.DownloadPath, StringComparison.OrdinalIgnoreCase))
                {
                    relativePath = relativePath.Substring(settings.DownloadPath.Length).TrimStart('/', '\\');
                }

                string localFilePath = _remotePathMappingService
                    .RemapRemoteToLocal(settings.Host, new OsPath(Path.Combine(settings.DownloadPath, relativePath)))
                    .FullPath;

                if (_diskProvider.FileExists(localFilePath))
                {
                    _diskProvider.DeleteFile(localFilePath);
                    _logger.Debug($"Deleted local file: {localFilePath}");
                }
                else
                {
                    _logger.Trace($"Local file not found or path not accessible, skipping deletion: {Path.GetFileName(file.Filename)}");
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, $"Failed to delete local file for {Path.GetFileName(file.Filename)}");
            }

            _logger.Trace($"Removed transfer {file.Id}");
        }));
    }

    private async Task CleanStaleDirectoriesAsync(string directoryPath, SlskdProviderSettings settings)
    {
        try
        {
            string localPath = _remotePathMappingService
                .RemapRemoteToLocal(settings.Host, new OsPath(Path.Combine(settings.DownloadPath, directoryPath)))
                .FullPath;

            await Task.Delay(1000);

            List<SlskdUserTransfers> all = await _apiClient.GetAllTransfersAsync(settings);
            bool hasRemaining = all.SelectMany(u => u.Directories)
                .Any(d => d.Directory.Equals(directoryPath, StringComparison.OrdinalIgnoreCase));

            if (hasRemaining)
            {
                _logger.Trace($"Directory {directoryPath} still has active downloads: skipping cleanup");
                return;
            }

            if (_diskProvider.FolderExists(localPath))
            {
                _logger.Debug($"Removing stale directory: {localPath}");
                _diskProvider.DeleteFolder(localPath, true);

                string? parent = Path.GetDirectoryName(localPath);
                if (!string.IsNullOrEmpty(parent) && _diskProvider.FolderExists(parent) && _diskProvider.FolderEmpty(parent))
                {
                    _logger.Info($"Removing empty parent directory: {parent}");
                    _diskProvider.DeleteFolder(parent, true);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"Error cleaning stale directories for path: {directoryPath}");
        }
    }

    private ReleaseInfo CreateReleaseInfoFromDirectory(string username, SlskdDownloadDirectory dir)
    {
        SlskdFolderData folderData = dir.CreateFolderData(username, _slskdItemsParser);
        SlskdSearchData searchData = new(null, null, false, false, 1, null);
        IGrouping<string, SlskdFileData> dirGroup = dir.ToSlskdFileDataList().GroupBy(_ => dir.Directory).First();
        AlbumData albumData = _slskdItemsParser.CreateAlbumData(string.Empty, dirGroup, searchData, folderData, null, 0);
        ReleaseInfo release = albumData.ToReleaseInfo();
        release.DownloadProtocol = null;
        return release;
    }

    private static string ExtractUsernameFromPath(string path)
    {
        string[] parts = path.TrimEnd('/').Split('/');
        return Uri.UnescapeDataString(parts[^1]);
    }

    private static List<(string Filename, long Size)> ParseFilesFromSource(string source)
    {
        using JsonDocument doc = JsonDocument.Parse(source);
        return doc.RootElement.EnumerateArray()
            .Select(el => (
                Filename: el.TryGetProperty("Filename", out JsonElement fn) ? fn.GetString() ?? "" : "",
                Size: el.TryGetProperty("Size", out JsonElement sz) ? sz.GetInt64() : 0L
            ))
            .ToList();
    }

    private SlskdDownloadItem? GetItem(int definitionId, string id) =>
        _downloadMappings.TryGetValue(new DownloadKey<int, string>(definitionId, id), out SlskdDownloadItem? item)
            ? item : null;

    private IEnumerable<SlskdDownloadItem> GetItemsForDef(int definitionId) =>
        _downloadMappings
            .Where(kvp => kvp.Key.OuterKey == definitionId)
            .Select(kvp => kvp.Value);

    private void AddItem(int definitionId, SlskdDownloadItem item) =>
        _downloadMappings[new DownloadKey<int, string>(definitionId, item.ID)] = item;

    private void RemoveItemFromDict(int definitionId, string id)
    {
        _downloadMappings.TryRemove(new DownloadKey<int, string>(definitionId, id), out _);
        _postProcessed.TryRemove(id, out _);
    }

    private HashSet<string> GetActiveUsernames(int definitionId) =>
        [.. GetItemsForDef(definitionId)
            .Where(i => i.Username != null)
            .Select(i => i.Username!)];
}
