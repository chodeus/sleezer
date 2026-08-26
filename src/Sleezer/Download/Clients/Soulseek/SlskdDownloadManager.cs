using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.History;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Music;
using NzbDrone.Plugin.Sleezer.Core.Model;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using NzbDrone.Plugin.Sleezer.Core.PostProcessing;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek;
using NzbDrone.Plugin.Sleezer.Metadata.FFmpeg;

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
    // Empty-directory sweep bookkeeping per definition ID.
    private readonly ConcurrentDictionary<int, DateTime> _lastEmptyDirSweepTimes = new();
    private readonly ConcurrentDictionary<int, int> _lastActiveDownloadCounts = new();
    private readonly ConcurrentDictionary<int, byte> _sweepInProgress = new();
    private static readonly TimeSpan SweepQuietPeriod = TimeSpan.FromMinutes(15);
    // Latest settings snapshot per definition ID: used by event-triggered retry callbacks
    private readonly ConcurrentDictionary<int, SlskdProviderSettings> _settingsCache = new();

    private readonly ISlskdApiClient _apiClient;
    private readonly IDownloadHistoryService _downloadHistoryService;
    private readonly IHistoryService _historyService;
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
    private readonly FFmpegPathResolver _ffmpeg;

    /// <summary>
    /// Tracks per-item post-processing completion so the owning item's status
    /// can't flip to Completed until the scan + tag pass has finished.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _postProcessed = new();

    public SlskdDownloadManager(
        ISlskdApiClient apiClient,
        IDownloadHistoryService downloadHistoryService,
        IHistoryService historyService,
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
        _historyService = historyService;
        _slskdItemsParser = slskdItemsParser;
        _remotePathMappingService = remotePathMappingService;
        _diskProvider = diskProvider;
        _logger = logger;
        _corruptionScanner = corruptionScanner;
        _preImportTagger = preImportTagger;
        _corruptionHandler = corruptionHandler;
        _metadataFactory = metadataFactory;
        _watchdog = watchdog;
        _ffmpeg = new FFmpegPathResolver(logger);
        _retryHandler = new SlskdRetryHandler(apiClient, NzbDroneLogger.GetLogger(typeof(SlskdRetryHandler)));
    }

    public async Task<string> DownloadAsync(RemoteAlbum remoteAlbum, int definitionId, SlskdProviderSettings settings)
    {
        _settingsCache[definitionId] = settings;

        SlskdDownloadItem item = new(remoteAlbum.Release)
        {
            ResolvedAlbum = remoteAlbum.Albums?.FirstOrDefault()
        };

        // Lidarr's TrackedDownloadService caches tracked downloads by downloadId
        // and keeps returning the cached object once its state leaves Downloading.
        // A re-grab of a failed release reuses the same stable content hash, hits
        // the cached DownloadFailed entry, and a completed retry is then silently
        // never imported (verified live 2026-07-21: grab → source fails → re-grab
        // → download completes → no import until Lidarr restarts). Suffix retries
        // so each attempt tracks under a fresh id; transfers still map back by
        // enqueued-file membership regardless of the id.
        string retryId = SlskdDownloadItem.ResolveRetryId(item.ID, id => _downloadHistoryService.GetLatestDownloadHistoryItem(id)?.EventType);
        List<SlskdDownloadItem> supersededAttempts = [];
        if (retryId != item.ID)
        {
            _logger.Debug("Release {Id} has terminal download history; tracking this attempt as {RetryId}", item.ID, retryId);
            item.ID = retryId;

            // Evict a stale attempt from tracking (its per-directory hash matches
            // first and would starve this retry). Its files are deleted only after a
            // successful enqueue (below), so a failed enqueue can't strand content.
            string? probeFile = item.FileData.FirstOrDefault(f => !string.IsNullOrEmpty(f.Filename))?.Filename;
            if (probeFile != null)
            {
                foreach (SlskdDownloadItem stale in GetItemsForDef(definitionId).Where(i => i.ID != retryId && i.OwnsFile(probeFile)).ToList())
                {
                    _logger.Debug("Evicting stale tracked attempt {StaleId} superseded by {RetryId}", stale.ID, retryId);
                    RemoveItemFromDict(definitionId, stale.ID);
                    supersededAttempts.Add(stale);
                }
            }
        }

        _logger.Trace("Download initiated: {Title} | Files: {FileCount}", remoteAlbum.Release.Title, item.FileData.Count);

        try
        {
            string username = ExtractUsernameFromPath(remoteAlbum.Release.DownloadUrl);
            List<(string Filename, long Size)> files = ParseFilesFromSource(remoteAlbum.Release.Source);

            // Always pin a release-derived destination: slskd otherwise keys the
            // local folder by the PEER'S directory name, so two peers sharing a
            // generic name ("_Unknown Album") collide and interleave sources.
            // Multi-disc additionally lands pre-merged, skipping the local move.
            string? destination = item.PreferredDestinationFolderName();

            SlskdEnqueueResult result = await _apiClient.EnqueueDownloadAsync(settings, username, files, externalId: item.ID, destination: destination);

            if (result.AllFailed)
                throw new DownloadClientException(
                    $"All {result.Failed.Count} files failed to enqueue: {string.Join("; ", result.Failed.Select(f => $"{Path.GetFileName(f.Filename)}: {f.Message}"))}");

            if (result.Failed.Count > 0)
            {
                _logger.Warn("{Failed} of {Total} files failed to enqueue for {Username}: {Messages}",
                    result.Failed.Count, files.Count, username, string.Join("; ", result.Failed.Select(f => f.Message).Distinct()));

                // Rejected files never produce a transfer — exclude them from
                // completion tracking so the item can still finish, import its
                // partial content, and let Lidarr re-search for the gaps.
                item.MarkEnqueueFailed(result.Failed.Select(f => f.Filename));
            }

            item.BatchId = result.BatchId;
            if (destination != null && result.BatchId != null)
            {
                // slskd honors the destination itself — no post-download merge.
                item.DerivedSubdirectory = destination;
                item.DiscFoldersMerged = true;
            }

            item.Username = username;
            SubscribeStateChanges(item, definitionId);
            AddItem(definitionId, item);

            // Delete superseded attempts' leftovers only after a successful enqueue
            // (slskd keys the local folder by remote NAME, so a failed attempt and
            // this retry can share one — stale leftovers interleave sources). Protect
            // the retry's own basenames so a shared file is never deleted.
            if (supersededAttempts.Count > 0)
            {
                HashSet<string> retryBasenames = item.FileData
                    .Select(f => Path.GetFileName((f.Filename ?? string.Empty).Replace('\\', '/')))
                    .Where(n => !string.IsNullOrEmpty(n))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                // Fire-and-forget: it self-bounds, protects retry files, and handles
                // its own errors, so it needn't delay the grab return.
                _ = CleanupSupersededAttemptsAsync(supersededAttempts, settings, retryBasenames);
            }

            return item.ID;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Download enqueue failed for {Title}", remoteAlbum.Release.Title);
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
                if (resolved.Status == DownloadItemStatus.Completed)
                    resolved = FailWhenCompletedFilesVanished(item, settings, resolved, now);
                if (resolved.Status == DownloadItemStatus.Completed && item.PostProcessFailure is { } postProcessFailure)
                    resolved = resolved with { Status = DownloadItemStatus.Failed, Message = postProcessFailure };
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
                _logger.Warn(ex, "Failed to build DownloadClientItem for {ItemId}. Skipping.", item.ID);
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
        Dictionary<string, long> ownedFileSizes = item.BuildOwnedFileSizes();

        // Run synchronously so Lidarr sees failures in its own log rather than
        // silently fire-and-forgetting the work behind its back.
        try
        {
            RemoveItemFilesAsync(item, settings).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.Warn(ex, "[def={DefinitionId}] Failed to remove slskd transfers / files for {Title}", definitionId, clientItem.Title);
        }

        TryDeleteOutputFolder(clientItem, ownedFileSizes, settings);
        TryDeleteUnmergedDiscFolders(item, settings);

        RemoveItemFromDict(definitionId, clientItem.DownloadId);

        if (settings.CleanStaleDirectories && !string.IsNullOrEmpty(directory))
            _ = CleanStaleDirectoriesAsync(directory, ownedFileSizes, settings);
    }

    /// <summary>
    /// Copies this grab's cue/log extras into the album folder Lidarr just
    /// imported into. Runs on AlbumImportedEvent, which fires before the
    /// download folder is cleaned up, so the source is still on disk.
    /// </summary>
    public void ImportExtrasForImportedAlbum(string downloadId, IReadOnlyCollection<string> importedTrackPaths)
    {
        try
        {
            // Most imports are other download clients' — miss quietly, before any disk work.
            KeyValuePair<DownloadKey<int, string>, SlskdDownloadItem> tracked = _downloadMappings
                .FirstOrDefault(kvp => string.Equals(kvp.Value.ID, downloadId, StringComparison.OrdinalIgnoreCase));
            SlskdDownloadItem? item = tracked.Value;
            if (item == null)
            {
                _logger.Trace("No tracked slskd item for {DownloadId}; nothing to import", downloadId);
                return;
            }

            IReadOnlyList<string> extras = item.NonAudioBasenames();
            if (extras.Count == 0)
            {
                _logger.Trace("[{ItemId}] Grab has no extra files to import", item.ID);
                return;
            }

            int definitionId = tracked.Key.OuterKey;
            if (!_settingsCache.TryGetValue(definitionId, out SlskdProviderSettings? settings))
            {
                _logger.Debug("[{ItemId}] No cached settings for definition {DefinitionId}; skipping extra import", item.ID, definitionId);
                return;
            }

            string root = GetRemoteDownloadPath(settings).FullPath.TrimEnd('/', '\\');
            string folder = item.GetFullFolderPath(new OsPath(root)).FullPath.TrimEnd('/', '\\');
            if (!IsStrictDescendantOfRoot(folder, root) || !_diskProvider.FolderExists(folder))
                folder = FindFolderOwningItemFiles(item, root) ?? string.Empty;

            // Defense in depth: the recovery path is basename-driven, so re-confine it.
            if (folder.Length == 0 || !IsStrictDescendantOfRoot(folder, root))
            {
                _logger.Warn("[{ItemId}] No download folder inside '{Root}' to read extras from; skipping extra import", item.ID, root);
                return;
            }

            string? destination = SlskdPathResolver.CommonParentDirectory(importedTrackPaths);
            if (string.IsNullOrEmpty(destination) || !_diskProvider.FolderExists(destination))
            {
                _logger.Warn("[{ItemId}] Imported tracks share no existing album folder; skipping extra import", item.ID);
                return;
            }

            CopyExtrasIntoAlbumFolder(item, extras, folder, destination);
        }
        catch (Exception ex)
        {
            _logger.Warn(ex, "Failed to import extra files for {DownloadId}", downloadId);
        }
    }

    /// <summary>
    /// Copies only extras this item conclusively owns (basename AND size), so a
    /// foreign file in a shared download folder is never pulled into the library.
    /// </summary>
    private void CopyExtrasIntoAlbumFolder(SlskdDownloadItem item, IReadOnlyList<string> extras, string folder, string destination)
    {
        Dictionary<string, long> ownedFileSizes = item.BuildOwnedFileSizes();
        Dictionary<string, string> onDisk = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in _diskProvider.GetFiles(folder, recursive: true))
            onDisk.TryAdd(Path.GetFileName(file), file);

        int copied = 0;
        foreach (string basename in extras)
        {
            if (!onDisk.TryGetValue(basename, out string? source))
            {
                _logger.Debug("[{ItemId}] Extra '{Extra}' not downloaded — skipping", item.ID, basename);
                continue;
            }

            if (!IsConclusivelyOwned(source, ownedFileSizes))
            {
                _logger.Debug("[{ItemId}] Extra '{Extra}' in '{Folder}' is not conclusively this download's — skipping", item.ID, basename, folder);
                continue;
            }

            try
            {
                // Overwrite mirrors track replacement on upgrade — a stale log
                // from the previous rip must not survive. Copy, never move:
                // Lidarr expects the download folder intact until it removes it.
                _diskProvider.CopyFile(source, Path.Combine(destination, basename), true);
                copied++;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "[{ItemId}] Failed to copy extra '{Extra}' into '{Folder}'", item.ID, basename, destination);
            }
        }

        if (copied > 0)
            _logger.Info("Imported {Count} extra file(s) into {Folder}", copied, destination);
    }

    private void TryDeleteOutputFolder(DownloadClientItem clientItem, Dictionary<string, long> ownedFileSizes, SlskdProviderSettings settings)
    {
        if (clientItem.OutputPath.IsEmpty)
        {
            _logger.Debug("[{Title}] No OutputPath recorded, skipping folder deletion", clientItem.Title);
            return;
        }

        string localRoot = _remotePathMappingService
            .RemapRemoteToLocal(settings.Host, new OsPath(settings.DownloadPath))
            .FullPath
            .TrimEnd('/', '\\');
        string folder = clientItem.OutputPath.FullPath.TrimEnd('/', '\\');

        // Safety: only delete a folder that canonicalizes to a strict descendant
        // of the remapped download root. This refuses both the bare root (a
        // never-populated SlskdDownloadDirectory reduces GetFullFolderPath to the
        // root) and any peer-controlled '..' traversal that resolves outside it.
        // Fail closed - if we can't prove containment, we don't delete.
        if (!IsStrictDescendantOfRoot(folder, localRoot))
        {
            _logger.Warn("[{Title}] OutputPath '{Folder}' is not inside the download root '{Root}', refusing to delete", clientItem.Title, folder, localRoot);
            return;
        }

        try
        {
            if (_diskProvider.FolderExists(folder))
            {
                _logger.Debug("[{Title}] Deleting folder '{Folder}'", clientItem.Title, folder);
                DeleteFolderGuardedByOwnership(folder, ownedFileSizes, clientItem.Title);
            }
            else if (_diskProvider.FileExists(folder))
            {
                _logger.Debug("[{Title}] Deleting file '{Folder}'", clientItem.Title, folder);
                _diskProvider.DeleteFile(folder);
            }
            else
            {
                _logger.Debug("[{Title}] Folder '{Folder}' already gone, skipping", clientItem.Title, folder);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn(ex, "[{Title}] Failed to delete output folder '{Folder}'", clientItem.Title, folder);
        }
    }

    /// <summary>
    /// Deletes the folder only when every audio file in it is conclusively this
    /// item's (basename AND size match — a same-named file another download
    /// overwrote in a shared folder fails the size check). Otherwise only the
    /// conclusively-owned files are deleted; ambiguity always retains data.
    /// </summary>
    private void DeleteFolderGuardedByOwnership(string folder, Dictionary<string, long> ownedFileSizes, string context)
    {
        List<string> files = _diskProvider.GetFiles(folder, recursive: true).ToList();
        List<string> ownedOnDisk = new();
        int foreignAudio = 0;

        foreach (string file in files)
        {
            if (!AudioFormatHelper.IsPostProcessAudioFile(file))
                continue;

            if (IsConclusivelyOwned(file, ownedFileSizes))
                ownedOnDisk.Add(file);
            else
                foreignAudio++;
        }

        if (foreignAudio == 0)
        {
            _diskProvider.DeleteFolder(folder, true);
            return;
        }

        _logger.Warn("[{Context}] '{Folder}' holds {Count} audio file(s) not conclusively this download's — deleting only this item's own files", context, folder, foreignAudio);
        foreach (string file in ownedOnDisk)
        {
            try
            {
                _diskProvider.DeleteFile(file);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "[{Context}] Failed to delete '{File}'", context, file);
            }
        }
    }

    private bool IsConclusivelyOwned(string file, Dictionary<string, long> ownedFileSizes)
    {
        if (!ownedFileSizes.TryGetValue(Path.GetFileName(file), out long expectedSize))
            return false;

        try
        {
            return _diskProvider.GetFileSize(file) == expectedSize;
        }
        catch (Exception)
        {
            // Unreadable size = unprovable ownership = keep the file.
            return false;
        }
    }

    /// <summary>
    /// Finds the root child actually holding the item's files. The batch
    /// destination lives only in memory, so a restart mid-download leaves
    /// GetFullFolderPath guessing the peer's source-directory name — this
    /// recovers the real folder by basename+size, the same ownership test the
    /// delete guards use. SelectOwningFolder applies the majority rule.
    /// </summary>
    private string? FindFolderOwningItemFiles(SlskdDownloadItem item, string root)
    {
        Dictionary<string, long> ownedFileSizes = item.BuildOwnedFileSizes();
        if (ownedFileSizes.Count == 0 || !_diskProvider.FolderExists(root))
            return null;

        List<(string Folder, int Matches)> candidates = new();

        foreach (string candidate in _diskProvider.GetDirectories(root))
        {
            try
            {
                candidates.Add((candidate, _diskProvider.GetFiles(candidate, recursive: true)
                    .Count(f => AudioFormatHelper.IsPostProcessAudioFile(f) && IsConclusivelyOwned(f, ownedFileSizes))));
            }
            catch (Exception ex)
            {
                _logger.Trace(ex, "[{ItemId}] Skipping '{Folder}' while locating download files", item.ID, candidate);
            }
        }

        return SlskdPathResolver.SelectOwningFolder(candidates, ownedFileSizes.Count);
    }

    /// <summary>
    /// Removes the per-disc local folders of a multi-disc item that was
    /// cancelled or failed before the post-completion merge ran.
    /// </summary>
    private void TryDeleteUnmergedDiscFolders(SlskdDownloadItem item, SlskdProviderSettings settings)
    {
        if (!item.IsMultiDirectory || item.DiscFoldersMerged)
            return;

        string localRoot = GetRemoteDownloadPath(settings).FullPath.TrimEnd('/', '\\');
        Dictionary<string, long> ownedFileSizes = item.BuildOwnedFileSizes();

        foreach (string leaf in item.RemoteDirectoryLeaves())
        {
            string folder = Path.Combine(localRoot, leaf);
            if (!IsStrictDescendantOfRoot(folder, localRoot) || !_diskProvider.FolderExists(folder))
                continue;

            try
            {
                _logger.Debug("[{ItemId}] Deleting unmerged disc folder '{Folder}'", item.ID, folder);
                DeleteFolderGuardedByOwnership(folder, ownedFileSizes, item.ID);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "[{ItemId}] Failed to delete unmerged disc folder '{Folder}'", item.ID, folder);
            }
        }
    }

    /// <summary>
    /// True only when <paramref name="candidate"/> canonicalizes to a strict
    /// descendant of <paramref name="root"/> (never equal to it). Both paths are
    /// resolved with Path.GetFullPath so any '..' segments are collapsed before
    /// the containment test, defeating peer-controlled directory traversal. Any
    /// resolution failure returns false (fail closed - never delete).
    /// </summary>
    private bool IsStrictDescendantOfRoot(string candidate, string root)
    {
        try
        {
            string fullRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullCandidate = Path.GetFullPath(candidate);

            // Ordinal, not OrdinalIgnoreCase: on Linux /downloads/MUSIC is a different
            // tree from /downloads/music, and this guard gates a delete. A false reject
            // leaves a folder behind; a false accept removes an unrelated one.
            string rootWithSep = fullRoot + Path.DirectorySeparatorChar;
            return fullCandidate.Length > rootWithSep.Length
                && fullCandidate.StartsWith(rootWithSep, StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            _logger.Warn(ex, "Path containment check failed for '{Candidate}' under '{Root}'; refusing to delete", candidate, root);
            return false;
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

        // Prune empty download shells when downloads have just DRAINED to idle
        // (imports have moved files out, so folders may now be empty) — the
        // natural trigger, not a blind timer. A daily backstop still runs for
        // the startup backlog and for slskd's own file-retention, which empties
        // folders while idle and fires no event sleezer can observe.
        int prevActive = _lastActiveDownloadCounts.GetOrAdd(definitionId, -1);
        _lastActiveDownloadCounts[definitionId] = activeUsernames.Count;
        bool drainedToIdle = prevActive > 0 && activeUsernames.Count == 0;

        // Seed the sweep clock on the FIRST poll after (re)start so the 24h
        // backstop measures from startup, not the epoch — otherwise the very
        // first poll would sweep before PollTransfersAsync has rehydrated the
        // tracked-item set, racing a not-yet-retracked import. The drain
        // trigger is unaffected (prevActive is -1 on the first poll anyway).
        if (prevActive < 0)
            _lastEmptyDirSweepTimes.TryAdd(definitionId, now);

        MaybePruneEmptyDownloadDirectories(definitionId, settings, now, drainedToIdle);
    }

    // Independent janitor for the gap neither slskd nor RemoveItem covers: slskd
    // deletes stale FILES (retention.files) but never their empty parent
    // directories, and RemoveItem only fires while an item is still tracked
    // in-memory — so a restart, an slskd retention purge, or an importFailed
    // item leaves an empty shell that accumulates forever. Prune recursively-
    // empty directories under the download root; folders that still hold data
    // are never touched (slskd's file-retention empties them first).
    // Prunes empty download shells — the gap neither slskd (deletes files, not
    // dirs) nor RemoveItem (fires only while an item is tracked) covers. The
    // single-flight gate below makes BOTH the throttle decision and the sweep
    // atomic per definition, so concurrent polls can't double-run.
    private void MaybePruneEmptyDownloadDirectories(int definitionId, SlskdProviderSettings settings, DateTime now, bool drainedToIdle)
    {
        if (!settings.CleanStaleDirectories)
            return;

        if (!_sweepInProgress.TryAdd(definitionId, 0))
            return;

        bool started = false;
        try
        {
            DateTime lastSweep = _lastEmptyDirSweepTimes.GetOrAdd(definitionId, DateTime.MinValue);
            // Primary trigger: downloads just drained to idle (imports moved
            // files out), with a 10-min floor against flapping. Backstop:
            // daily, for the startup backlog and slskd's own idle file-retention.
            bool due = (drainedToIdle && now - lastSweep >= TimeSpan.FromMinutes(10))
                       || now - lastSweep >= TimeSpan.FromHours(24);
            if (!due)
                return;

            _lastEmptyDirSweepTimes[definitionId] = now;
            started = true;

            // Off the poll thread — a recursive scan + delete over every root
            // child must not stall Lidarr's status polling.
            _ = Task.Run(() =>
            {
                try { PruneEmptyDownloadDirectories(definitionId, settings, now); }
                catch (Exception ex) { _logger.Warn(ex, "[def={DefinitionId}] Empty download-directory sweep failed", definitionId); }
                finally { _sweepInProgress.TryRemove(definitionId, out _); }
            });
        }
        finally
        {
            if (!started)
                _sweepInProgress.TryRemove(definitionId, out _);
        }
    }

    private void PruneEmptyDownloadDirectories(int definitionId, SlskdProviderSettings settings, DateTime now)
    {
        string root = _remotePathMappingService
            .RemapRemoteToLocal(settings.Host, new OsPath(settings.DownloadPath))
            .FullPath
            .TrimEnd('/', '\\');

        if (string.IsNullOrEmpty(root) || !_diskProvider.FolderExists(root))
            return;

        // Never touch a folder a tracked item owns, even if momentarily empty.
        HashSet<string> trackedLeaves = new(StringComparer.OrdinalIgnoreCase);
        foreach (SlskdDownloadItem item in GetItemsForDef(definitionId))
        {
            string name = item.LocalAlbumFolderName();
            if (!string.IsNullOrEmpty(name))
                trackedLeaves.Add(name);
            foreach (string leaf in item.RemoteDirectoryLeaves())
                trackedLeaves.Add(leaf);

            // Batch destinations / pattern-derived folders live under their own
            // names — protect their root-child segment too.
            foreach (string? sub in new[] { item.ConfirmedSubdirectory, item.DerivedSubdirectory })
            {
                string? first = sub?.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrEmpty(first))
                    trackedLeaves.Add(first);
            }
        }

        EmptyDownloadDirectorySweeper.Prune(root, trackedLeaves, SweepQuietPeriod, now, _logger);
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

        _logger.Debug("[def={DefinitionId}] Polled {UserCount} users | Tracked: {Tracked}", definitionId, activeUsernames.Count, currentIdSet.Count);

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
                _logger.Warn(ex, "[def={DefinitionId}] Watchdog failed for item {ItemId}", definitionId, item.ID);
            }
        }

        if (settings.Inclusive)
        {
            foreach (SlskdDownloadItem item in GetItemsForDef(definitionId)
                .Where(i => !currentIdSet.ContainsKey(i.ID) && i.ReleaseInfo.DownloadProtocol == null)
                .ToList())
            {
                _logger.Trace("[def={DefinitionId}] Pruning inclusive item {ItemId} (gone from Slskd)", definitionId, item.ID);
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

            foreach ((SlskdDownloadItem item, SlskdDownloadDirectory slice) in ResolveDirectoryOwners(definitionId, settings, userTransfers.Username, dir))
            {
                currentIdSet.TryAdd(item.ID, true);
                ApplyDirectoryToItem(settings, userTransfers.Username, item, slice);
            }
        }
    }

    /// <summary>
    /// Every tracked item with a stake in one peer directory, each paired with
    /// just its own transfers — two Lidarr albums can share a peer directory.
    /// </summary>
    private List<(SlskdDownloadItem Item, SlskdDownloadDirectory Slice)> ResolveDirectoryOwners(
        int definitionId,
        SlskdProviderSettings settings,
        string username,
        SlskdDownloadDirectory dir)
    {
        SlskdDirectoryPartition partition = SlskdDirectoryPartitioner.Partition(dir, GetItemsForDef(definitionId), username);

        // A multi-disc item's ID hashes ALL its files, but slskd reports transfers
        // per remote directory — that hash only hits for a single-directory item.
        SlskdDownloadItem? keyed = GetItem(definitionId, SlskdDownloadItem.GetStableMD5Id(dir.Files?.Select(f => f.Filename) ?? []));
        List<(SlskdDownloadItem Item, SlskdDownloadDirectory Slice)> owners = SlskdDirectoryPartitioner.WithKeyedOwner(partition, keyed, dir);

        if (partition.Unclaimed is { } unclaimed &&
            AdoptUnknownDirectory(definitionId, settings, username, unclaimed) is { } adopted)
            owners.Add((adopted, unclaimed));

        return owners;
    }

    /// <summary>
    /// Re-attaches transfers no tracked item claimed to the grab that started
    /// them, or (inclusive mode) builds an item from the directory itself.
    /// </summary>
    private SlskdDownloadItem? AdoptUnknownDirectory(
        int definitionId,
        SlskdProviderSettings settings,
        string username,
        SlskdDownloadDirectory dir)
    {
        string hash = SlskdDownloadItem.GetStableMD5Id(dir.Files?.Select(f => f.Filename) ?? []);
        _logger.Trace("[def={DefinitionId}] Unknown item {Hash}: checking history", definitionId, hash);
        DownloadHistory? history = FindGrabOwningDirectory(hash, dir);

        SlskdDownloadItem? item = null;
        if (history != null)
        {
            item = new SlskdDownloadItem(history.Release)
            {
                // The grab's id, not the recomputed content hash: a
                // retry grab carries an -rN suffix and a multi-disc
                // grab hashes ALL discs — Lidarr tracks both under
                // the id it was handed at grab time.
                ID = history.DownloadId
            };
        }
        else if (settings.Inclusive)
        {
            item = new SlskdDownloadItem(CreateReleaseInfoFromDirectory(username, dir));
        }

        if (item == null)
            return null;

        // AddItem overwrites by ID, and the history probe matches on the enqueued
        // set — so a live item's own rejected file would rebuild it here and drop
        // the transfer state it has accumulated.
        if (GetItem(definitionId, item.ID) != null)
            return null;

        SubscribeStateChanges(item, definitionId);
        AddItem(definitionId, item);
        return item;
    }

    /// <summary>Attaches one item's share of a peer directory and its derived state.</summary>
    private void ApplyDirectoryToItem(
        SlskdProviderSettings settings,
        string username,
        SlskdDownloadItem item,
        SlskdDownloadDirectory dir)
    {
        item.Username ??= username;
        item.SlskdDownloadDirectory = dir;

        // slskd echoes the batch id on every transfer — the only copy that
        // survives a restart. A batch always carried a destination, so slskd
        // placed the discs pre-merged: restoring both stops a needless merge
        // from moving a same-named root folder belonging to another download,
        // and lets the ConfirmedSubdirectory gate in HandleEventAsync trust a
        // multi-disc completion.
        if (item.BatchId == null &&
            dir.Files?.Select(f => f.BatchId).FirstOrDefault(id => !string.IsNullOrEmpty(id)) is { } recoveredBatchId)
        {
            item.BatchId = recoveredBatchId;
            item.DiscFoldersMerged = true;
        }

        // With a non-default subdirectory pattern, slskd places transfers
        // somewhere the leaf-name guess can't predict — derive it.
        if (item.DerivedSubdirectory == null && item.ConfirmedSubdirectory == null &&
            settings.GetDestinationConfig() is { UsesDefaultPattern: false } destinationConfig &&
            dir.Files?.FirstOrDefault()?.Filename is { Length: > 0 } firstFile)
        {
            item.DerivedSubdirectory = SlskdPathResolver.ResolveSubdirectory(
                destinationConfig,
                username,
                firstFile,
                item.BatchId,
                item.BatchId != null ? item.ID : null);
        }

        // Fallback trigger for post-process: if the transfer poll sees all
        // files completed before the event poll has caught up with the matching
        // DownloadDirectoryComplete event, enqueue here. Without this, Lidarr
        // can see status=Completed and start importing before the scan runs.
        // _postProcessed.TryAdd dedupes against the event-path trigger.
        if (item.AllAcceptedFilesCompleted())
            EnqueuePostProcess(item, settings);
    }

    // Restart re-attach: -rN retry and multi-disc grab ids never equal the
    // per-directory hash, so fall back to file-membership over recent grabs.
    private DownloadHistory? FindGrabOwningDirectory(string hash, SlskdDownloadDirectory dir)
    {
        // A direct hit on a POISONED id (its latest history event is terminal)
        // would re-attach a completed retry under the failed first attempt —
        // the exact wedge ResolveRetryId exists to avoid. Fall through to the
        // newest-first probe, which finds the -rN retry grab instead.
        DownloadHistory? direct = _downloadHistoryService.GetLatestGrab(hash);
        if (direct?.Release != null &&
            string.Equals(direct.Protocol, nameof(SoulseekDownloadProtocol), StringComparison.OrdinalIgnoreCase) &&
            !SlskdDownloadItem.IsPoisonedHistoryEvent(_downloadHistoryService.GetLatestDownloadHistoryItem(direct.DownloadId)?.EventType))
            return direct;

        string? probeFile = dir.Files?.FirstOrDefault()?.Filename;
        if (string.IsNullOrEmpty(probeFile))
            return null;

        IEnumerable<string> recentGrabIds = _historyService.Since(DateTime.UtcNow.AddDays(-14), EntityHistoryEventType.Grabbed)
            .OrderByDescending(h => h.Date)
            .Select(h => h.DownloadId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        DownloadHistory? poisonedFallback = null;
        foreach (string downloadId in recentGrabIds)
        {
            DownloadHistory? grab = _downloadHistoryService.GetLatestGrab(downloadId);
            if (grab?.Release == null || !string.Equals(grab.Protocol, nameof(SoulseekDownloadProtocol), StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                if (!new SlskdDownloadItem(grab.Release).OwnsFile(probeFile))
                    continue;

                if (!SlskdDownloadItem.IsPoisonedHistoryEvent(_downloadHistoryService.GetLatestDownloadHistoryItem(grab.DownloadId)?.EventType))
                    return grab;

                poisonedFallback ??= grab;
            }
            catch (Exception ex)
            {
                _logger.Trace(ex, "Skipping grab {DownloadId} while probing ownership of {File}", downloadId, probeFile);
            }
        }

        // Visibility beats limbo: a poisoned re-attach at least surfaces the
        // transfer in the queue instead of leaving it orphaned in slskd.
        return poisonedFallback;
    }

    private static readonly TimeSpan CompletedFolderGracePeriod = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CompletedFolderCheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A Completed item whose files are gone (slskd retention, a shared-folder
    /// cleanup, manual deletion) would otherwise sit in Lidarr's queue forever
    /// with nothing to import — fail it so the release is blocklisted and
    /// re-searched. The grace period rides out slskd's incomplete→downloads
    /// move and Lidarr's own import-then-remove window; anything already
    /// imported is left alone.
    /// </summary>
    private SlskdStatusResolver.DownloadStatus FailWhenCompletedFilesVanished(
        SlskdDownloadItem item,
        SlskdProviderSettings settings,
        SlskdStatusResolver.DownloadStatus resolved,
        DateTime utcNow)
    {
        try
        {
            string root = GetRemoteDownloadPath(settings).FullPath.TrimEnd('/', '\\');
            string folder = item.GetFullFolderPath(new OsPath(root)).FullPath.TrimEnd('/', '\\');

            // Bare-root output means the item never resolved a folder — not judgeable.
            if (!IsStrictDescendantOfRoot(folder, root))
                return resolved;

            // Throttle the disk scan off the status-polling hot path; between
            // scans the cached missing-since state carries the decision.
            bool scanDue = item.CompletedFolderCheckedAtUtc is not { } lastChecked ||
                           utcNow - lastChecked >= CompletedFolderCheckInterval;
            if (scanDue)
            {
                item.CompletedFolderCheckedAtUtc = utcNow;

                bool hasAudio = _diskProvider.FolderExists(folder) &&
                                _diskProvider.GetFiles(folder, recursive: true).Any(AudioFormatHelper.IsPostProcessAudioFile);
                if (hasAudio)
                {
                    item.CompletedFolderMissingSince = null;
                    item.CompletedFolderFailureLogged = false;
                    return resolved;
                }

                // Missing folder is far more often a wrong guess than lost data
                // — relocate before condemning the release. Only until the
                // failure is reported: the scan walks every root child, and a
                // genuinely deleted download would repeat it forever. Skipped
                // while discs are unmerged: matching one disc folder would pin
                // ConfirmedSubdirectory to it and strand the rest.
                if (!item.CompletedFolderFailureLogged &&
                    !item.AwaitingDiscMerge &&
                    FindFolderOwningItemFiles(item, root) is { } actualFolder &&
                    SlskdPathResolver.MakeRelativeToDownloads(root, actualFolder) is { Length: > 0 } relative)
                {
                    _logger.Info("[{ItemId}] Files are in '{Actual}', not '{Expected}' — correcting the output path",
                        item.ID, actualFolder, folder);
                    // GetItems reads OutputPath from GetFullFolderPath after this
                    // returns, so the corrected path lands on the same poll.
                    item.ConfirmedSubdirectory = relative;
                    item.CompletedFolderMissingSince = null;
                    item.CompletedFolderFailureLogged = false;
                    return resolved;
                }

                item.CompletedFolderMissingSince ??= utcNow;
            }

            if (item.CompletedFolderMissingSince is not { } missingSince ||
                utcNow - missingSince < CompletedFolderGracePeriod)
                return resolved;

            // Poisoned = Lidarr already resolved it (incl. pending ImportIncomplete
            // — spared here since v1.11.4); re-failing would only re-fire ghosts.
            DownloadHistoryEventType? latest = _downloadHistoryService.GetLatestDownloadHistoryItem(item.ID)?.EventType;
            if (SlskdDownloadItem.IsPoisonedHistoryEvent(latest))
                return resolved;

            if (!item.CompletedFolderFailureLogged)
            {
                item.CompletedFolderFailureLogged = true;
                _logger.Warn("[{ItemId}] Completed download has no audio left in '{Folder}' — reporting failure so the release is retried", item.ID, folder);
            }
            else
            {
                _logger.Trace("[{ItemId}] Still no audio in '{Folder}' — keeping the failed status", item.ID, folder);
            }

            return resolved with
            {
                Status = DownloadItemStatus.Failed,
                Message = $"Downloaded files are no longer present in '{folder}'"
            };
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Could not verify completed files for {ItemId}; leaving status as-is", item.ID);
            return resolved;
        }
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
                _logger.Warn(ex, "[def={DefinitionId}] Failed to process event {EventType} ({EventId})", definitionId, record.Type, record.Id);
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
            string? localDir = doc.RootElement.TryGetProperty("localDirectoryName", out JsonElement ldn) ? ldn.GetString() : null;

            _logger.Trace("[def={DefinitionId}] Event DownloadDirectoryComplete: {RemoteDir} by {Username}: forcing refresh", definitionId, remoteDir, username);

            // Refresh transfer state FIRST so any item whose SlskdDownloadDirectory
            // hasn't been populated yet (common for downloads that complete between
            // 30s poll cycles) gets its directory filled in before we look it up.
            SlskdUserTransfers? userTransfers = await _apiClient.GetUserTransfersAsync(settings, username);
            if (userTransfers != null)
                ProcessUserTransfers(definitionId, settings, userTransfers, new ConcurrentDictionary<string, bool>());

            SlskdDownloadItem? item = GetItemsForDef(definitionId)
                .FirstOrDefault(i => i.Username == username &&
                                     (i.TracksRemoteDirectory(remoteDir) ||
                                      string.Equals(i.SlskdDownloadDirectory?.Directory, remoteDir, StringComparison.OrdinalIgnoreCase)));

            if (item != null)
            {
                // slskd tells us exactly where it put the files — ground truth
                // for OutputPath, especially under custom subdirectory patterns
                // or batch destinations. Single-directory items only: for
                // multi-disc the per-disc local folder is not the album folder.
                if (!item.IsMultiDirectory || item.BatchId != null)
                {
                    item.ConfirmedSubdirectory = SlskdPathResolver.MakeRelativeToDownloads(settings.DownloadPath, localDir)
                        ?? item.ConfirmedSubdirectory;
                }

                // Multi-disc items get one DownloadDirectoryComplete per disc;
                // only post-process once every enqueued file is done.
                if (item.AllAcceptedFilesCompleted())
                    EnqueuePostProcess(item, settings);
                else
                    _logger.Trace("[def={DefinitionId}] Directory {RemoteDir} complete but item {ItemId} still has pending files — waiting", definitionId, remoteDir, item.ID);
            }
            else if (userTransfers == null)
            {
                // slskd has no active transfers for this user — the event log is
                // replaying an old completion whose transfer was already cleaned
                // up. Nothing to post-process; stay quiet.
                _logger.Trace("[def={DefinitionId}] DownloadDirectoryComplete for {RemoteDir} by {Username}: stale replayed event, no active transfer at slskd — skipping", definitionId, remoteDir, username);
            }
            else
            {
                _logger.Warn("[def={DefinitionId}] DownloadDirectoryComplete for {RemoteDir} by {Username} but no tracked item matched — skipping post-process", definitionId, remoteDir, username);
            }
        }
        else if (record.Type == SlskdEventTypes.DownloadFileComplete && _logger.IsTraceEnabled)
        {
            using JsonDocument doc = JsonDocument.Parse(record.Data);
            if (doc.RootElement.TryGetProperty("transfer", out JsonElement transferEl))
            {
                string filename = transferEl.TryGetProperty("filename", out JsonElement fn) ? fn.GetString() ?? "" : "";
                string username = transferEl.TryGetProperty("username", out JsonElement un) ? un.GetString() ?? "" : "";
                _logger.Trace("[def={DefinitionId}] Event DownloadFileComplete: {Filename} by {Username}", definitionId, Path.GetFileName(filename), username);
            }
        }
    }

    // Per-file ffmpeg decode timeout. Hardcoded: 120s handles any real track;
    // corrupt files that hang ffmpeg get killed at this limit.
    // Issue #85: a peer's advertised bit depth is never checked before the transfer,
    // and cannot be — Soulseek exposes no file header. Once the bytes are on disk they
    // can be read, so the operator is told what actually arrived rather than what was
    // claimed. Deliberately reports only: a file that is not what it claimed is still a
    // good file, Lidarr re-detects quality at import, and failing here would delete a
    // usable album over a label.
    private void VerifyAdvertisedQuality(SlskdDownloadItem item, string folderPath)
    {
        try
        {
            string[] audioFiles = [.. _diskProvider.GetFiles(folderPath, recursive: true).Where(AudioFormatHelper.IsPostProcessAudioFile)];
            if (audioFiles.Length == 0)
                return;

            AudioQualityReading actual = AudioQualityVerifier.Read(audioFiles, _logger);
            int? advertisedDepth = SlskdItemsParser.UnanimousOrNull(item.FileData, f => f.BitDepth);
            int? advertisedRate = SlskdItemsParser.UnanimousOrNull(item.FileData, f => f.SampleRate);

            switch (AudioQualityVerifier.Compare(advertisedDepth, advertisedRate, actual))
            {
                case QualityVerdict.Overstated:
                    _logger.Warn("[quality-verify] {ItemId}: advertised {AdvertisedDepth}-bit/{AdvertisedRate}Hz but the files are {ActualDepth}-bit/{ActualRate}Hz across {FileCount} file(s). The download is kept — Lidarr re-detects quality on import — but it is not what the peer said it was.",
                        item.ID, advertisedDepth, advertisedRate, actual.BitDepth, actual.SampleRate, actual.FilesRead);
                    break;

                case QualityVerdict.Mixed:
                    _logger.Warn("[quality-verify] {ItemId}: the downloaded files do not share one bit depth or sample rate, so no single quality claim describes this folder ({FileCount} file(s) read).",
                        item.ID, actual.FilesRead);
                    break;

                case QualityVerdict.Matches:
                    _logger.Debug("[quality-verify] {ItemId}: verified {ActualDepth}-bit/{ActualRate}Hz across {FileCount} file(s)",
                        item.ID, actual.BitDepth, actual.SampleRate, actual.FilesRead);
                    break;

                default:
                    _logger.Debug("[quality-verify] {ItemId}: nothing to compare — advertised depth {AdvertisedDepth}, read {FileCount} file(s)",
                        item.ID, advertisedDepth, actual.FilesRead);
                    break;
            }
        }
        catch (Exception ex)
        {
            // Reporting only; never let it affect the download's outcome.
            _logger.Debug(ex, "[quality-verify] {ItemId}: verification pass failed", item.ID);
        }
    }

    private void EnqueuePostProcess(SlskdDownloadItem item, SlskdProviderSettings settings)
    {
        // Guard against duplicate post-process tasks: slskd can emit both
        // DownloadDirectoryComplete and a synthetic refresh-triggered completion.
        if (!_postProcessed.TryAdd(item.ID, 0))
            return;

        // Terminal rehydrated ghosts (slskd retains transfers ~24h) have nothing
        // left to scan or tag — their folders are legitimately gone.
        DownloadHistoryEventType? latest = _downloadHistoryService.GetLatestDownloadHistoryItem(item.ID)?.EventType;
        if (SlskdDownloadItem.IsTerminalDownloadEvent(latest))
        {
            _logger.Debug("[post-process] Skipping {ItemId}: download history is terminal ({Event})", item.ID, latest);
            return;
        }

        FFmpegSettings? sharedSettings;
        try
        {
            sharedSettings = PostProcessRunner.ReadSharedSettings(_metadataFactory);
        }
        catch (Exception ex)
        {
            // Fail closed: the old null return was indistinguishable from "both toggles
            // off", so a transient read imported an album that was never scanned.
            item.PostProcessFailure = "Post-processing settings could not be read";
            _logger.Error(ex, "[post-process] {ItemId}: settings unreadable; failing the item rather than importing it unscanned", item.ID);
            return;
        }

        if (sharedSettings == null)
        {
            item.PostProcessFailure = "Post-processing settings are missing";
            _logger.Error("[post-process] {ItemId}: no FFmpeg metadata definition found; failing the item rather than importing it unverified", item.ID);
            return;
        }

        bool scanEnabled = sharedSettings?.CorruptionScanClients?.Contains((int)PostProcessClient.Slskd) ?? false;
        bool tagEnabled = sharedSettings?.PreImportTaggingClients?.Contains((int)PostProcessClient.Slskd) ?? false;
        bool mergeNeeded = item.IsMultiDirectory && !item.DiscFoldersMerged;

        if (!scanEnabled && !tagEnabled && !mergeNeeded)
            return;

        Task task = Task.Run(async () =>
        {
            using CancellationTokenSource cts = new(TimeSpan.FromMinutes(settings.PostProcessingTimeoutMinutes));

            try
            {
                // Sanity delay: slskd reports DownloadDirectoryComplete before it
                // finishes moving files out of the incomplete dir. Give it a moment.
                await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);

                // Multi-disc: slskd downloaded each remote disc directory into
                // its own local folder (CD1, CD2). Merge them into the album
                // folder BEFORE scanning/importing — the item's OutputPath
                // (GetFullFolderPath) already points at the merged folder, and
                // status stays Downloading until this task completes, so Lidarr
                // cannot import mid-merge.
                if (mergeNeeded)
                {
                    TryMergeDiscFolders(item, settings);

                    if (!item.DiscFoldersMerged)
                    {
                        // Fail closed: the folder is missing discs, and _postProcessed means
                        // no later pass will merge them. FailWhenCompletedFilesVanished does
                        // not catch this — the discs that did move leave audio behind.
                        item.PostProcessFailure = "Disc-folder merge incomplete; the album is missing discs";
                        _logger.Warn("[post-process] {ItemId}: failing — disc-folder merge incomplete, scan/tag skipped", item.ID);
                        return;
                    }
                }

                OsPath remotePath = GetRemoteDownloadPath(settings);
                string folderPath = item.GetFullFolderPath(remotePath).FullPath;

                if (!_diskProvider.FolderExists(folderPath))
                {
                    _logger.Warn("[post-process] Folder missing after DownloadDirectoryComplete: {FolderPath}", folderPath);
                    return;
                }

                if (!scanEnabled && !tagEnabled)
                    return;

                if (scanEnabled)
                {
                    // 1. Corruption scan across every audio file in the folder.
                    List<SlskdCorruptionStrike> strikes = await ScanFolderForCorruptionAsync(item, folderPath, sharedSettings?.FFmpegPath, cts.Token);

                    if (strikes.Count > 0)
                    {
                        // 2a. Corruption path: delete folder, short-circuit retries,
                        //     publish DownloadFailedEvent. Skip tagging (file is gone).
                        await _corruptionHandler.HandleCorruptDownloadAsync(item, strikes, folderPath, settings, cts.Token);
                        return;
                    }

                    // The files are readable, so they can now answer for themselves.
                    VerifyAdvertisedQuality(item, folderPath);
                }

                if (tagEnabled)
                {
                    Album? album = item.ResolvedAlbum;
                    Artist? artist = album?.Artist?.Value;
                    if (album == null || artist == null)
                    {
                        _logger.Debug("[post-process] Pre-import tag: skipping {ItemId}; no ResolvedAlbum/Artist (item likely reconstructed from history in inclusive mode).", item.ID);
                        return;
                    }

                    // 2b. Clean path: run Lidarr-backed pre-import tagging.
                    // Pass null for albumRelease so PreImportTagger lets
                    // Lidarr's CandidateService rank releases by track-count
                    // distance — forcing the monitored release here was the
                    // cause of "missing tracks" import failures when the
                    // download is a different edition than the monitored one.
                    PreImportTagger.TaggingResult tagResult = await _preImportTagger.TagCompletedDownloadAsync(
                        album,
                        artist,
                        albumRelease: null,
                        item.ID,
                        folderPath,
                        PostProcessRunner.TagConfidenceThreshold,
                        sharedSettings?.StripFeaturedArtists ?? false,
                        cts.Token,
                        verifyAllWithFingerprint: settings.VerifyImportsWithFingerprint,
                        fingerprintTitleFallback: true);

                    // Refresh ownership identities after tagging — see
                    // SlskdDownloadItem._taggedFileSizes.
                    RecordTaggedFileIdentities(item, tagResult.TaggedFiles);
                }
            }
            catch (OperationCanceledException)
            {
                // Fail closed, as above: the pass never finished, so nothing here has
                // vouched for the files Lidarr is about to import.
                item.PostProcessFailure = "Post-processing timed out before the files were verified";
                _logger.Warn("[post-process] Timed out for {ItemId}", item.ID);
            }
            catch (Exception ex)
            {
                item.PostProcessFailure = "Post-processing failed before the files were verified";
                _logger.Error(ex, "[post-process] Unexpected failure for {ItemId}", item.ID);
            }
        });

        item.PostProcessTasks.Add(task);
    }

    private void RecordTaggedFileIdentities(SlskdDownloadItem item, IReadOnlyList<PreImportTagger.TaggedFile>? taggedFiles)
    {
        if (taggedFiles == null)
            return;

        foreach (PreImportTagger.TaggedFile tagged in taggedFiles)
        {
            try
            {
                item.RecordTaggedFile(Path.GetFileName(tagged.FinalPath), _diskProvider.GetFileSize(tagged.FinalPath));
            }
            catch (Exception ex)
            {
                // Unrecorded = the old fail-closed behavior (file retained).
                _logger.Debug(ex, "[post-process] Could not record tagged identity for {Path}", tagged.FinalPath);
            }
        }
    }

    /// <summary>
    /// Moves the per-disc local folders slskd created (CD1, CD2, ...) into the
    /// item's album folder, preserving the disc subfolders (Lidarr imports
    /// multi-disc layouts fine as long as they share one root). Every move is
    /// gated on the resolved paths being strict descendants of the download
    /// root — disc leaf names are peer-controlled.
    /// </summary>
    private void TryMergeDiscFolders(SlskdDownloadItem item, SlskdProviderSettings settings)
    {
        try
        {
            string localRoot = GetRemoteDownloadPath(settings).FullPath.TrimEnd('/', '\\');
            string albumFolder = item.GetFullFolderPath(new OsPath(localRoot)).FullPath;

            if (!IsStrictDescendantOfRoot(albumFolder, localRoot))
            {
                _logger.Warn("[merge] Album folder '{Album}' is not inside the download root '{Root}', refusing disc merge", albumFolder, localRoot);
                return;
            }

            bool allMoved = true;

            foreach (string leaf in item.RemoteDirectoryLeaves())
            {
                string source = Path.Combine(localRoot, leaf);
                // PathEquals, not OrdinalIgnoreCase: on Linux /downloads/CD1 and /downloads/cd1
                // are different folders, and equating them skips a real disc while still
                // recording the merge complete. It also normalizes unicode, which peer-supplied
                // leaf names carry.
                bool leafIsAlbumFolder = Path.GetFullPath(source).PathEquals(Path.GetFullPath(albumFolder));

                if (!_diskProvider.FolderExists(source))
                {
                    // An absent source is normal once an earlier pass moved it — but only if
                    // the album folder actually holds it now. Neither present means the disc
                    // never arrived, and recording a merge would hide that. A wholly missing
                    // album folder is left to FailWhenCompletedFilesVanished, which relocates
                    // before condemning.
                    if (!leafIsAlbumFolder && !_diskProvider.FolderExists(Path.Combine(albumFolder, leaf)))
                    {
                        allMoved = false;
                        _logger.Warn("[merge] {ItemId}: disc folder '{Leaf}' is in neither the download root nor the album folder", item.ID, leaf);
                    }

                    continue;
                }

                if (!IsStrictDescendantOfRoot(source, localRoot))
                {
                    allMoved = false;
                    continue;
                }

                if (leafIsAlbumFolder)
                    continue;

                string target = Path.Combine(albumFolder, leaf);
                if (_diskProvider.FolderExists(target))
                    continue;

                if (!IsStrictDescendantOfRoot(target, localRoot))
                {
                    allMoved = false;
                    continue;
                }

                try
                {
                    if (!_diskProvider.FolderExists(albumFolder))
                        _diskProvider.CreateFolder(albumFolder);

                    _logger.Debug("[merge] {ItemId}: moving disc folder '{Source}' -> '{Target}'", item.ID, source, target);
                    _diskProvider.MoveFolder(source, target);
                }
                catch (Exception ex)
                {
                    allMoved = false;
                    _logger.Warn(ex, "[merge] {ItemId}: failed to move disc folder '{Source}'", item.ID, source);
                }
            }

            // A partial merge must NOT be recorded as merged: the flag guards
            // both the post-process scan (which would see a disc missing) and
            // TryDeleteUnmergedDiscFolders cleanup of the leftover folder.
            item.DiscFoldersMerged = allMoved;
            if (!allMoved)
                _logger.Warn("[merge] {ItemId}: disc-folder merge incomplete; leaving unmerged folders in place", item.ID);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[merge] Disc-folder merge failed for {ItemId}", item.ID);
        }
    }

    private async Task<List<SlskdCorruptionStrike>> ScanFolderForCorruptionAsync(
        SlskdDownloadItem item,
        string folderPath,
        string? ffmpegPath,
        CancellationToken ct)
    {
        List<SlskdCorruptionStrike> strikes = new();

        // Re-apply the path every scan: Xabe's global state resets on Lidarr restart, and
        // the provider's Test only writes it for the current run.
        await _ffmpeg.ResolveAsync(ffmpegPath, ct);

        // Only scan audio files. Non-audio artifacts (covers, nfos, logs) would
        // fail TagLib parsing and incorrectly flag the whole album as corrupt.
        string[] audioFiles = _diskProvider.GetFiles(folderPath, recursive: true)
            .Where(AudioFormatHelper.IsPostProcessAudioFile)
            .ToArray();
        if (audioFiles.Length == 0)
        {
            _logger.Info("[post-process] {ItemId}: no audio files in {FolderPath}; nothing to scan", item.ID, folderPath);
            return strikes;
        }

        _logger.Info("[post-process] {ItemId}: scanning {Count} audio file(s) in {FolderPath}", item.ID, audioFiles.Length, folderPath);
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
                CorruptionScanner.Result r = await _corruptionScanner.ScanAsync(path, PostProcessRunner.CorruptionScanTimeoutSeconds, ct);
                return (path, r);
            }
            finally { gate.Release(); }
        }).ToArray();

        // Await everything before inspecting: iterating with await rethrows on the first
        // failure and leaves `using` to dispose the gate while siblings are still waiting
        // on it, turning one scan error into a pile of unobserved ObjectDisposedExceptions.
        (string path, CorruptionScanner.Result result)[] scanned = await Task.WhenAll(tasks);

        foreach ((string path, CorruptionScanner.Result result) in scanned)
        {
            if (!result.IsCorrupt)
                continue;

            // Map disk path back to the SlskdFileState for retry short-circuit.
            string filename = Path.GetFileName(path);
            SlskdFileState? fileState = item.FileStates.Values
                .FirstOrDefault(fs => string.Equals(Path.GetFileName(fs.File.Filename), filename, StringComparison.OrdinalIgnoreCase));

            if (fileState == null)
                _logger.Warn("[post-process] Corrupt file {Filename} not mapped to any tracked slskd transfer; counting as corruption but skipping retry short-circuit.", filename);

            strikes.Add(new SlskdCorruptionStrike(fileState, result.Reason));
        }

        watch.Stop();
        _logger.Info("[post-process] {ItemId}: scan complete in {Seconds:F1}s \u2014 {Count} file(s), {Strikes} corrupt", item.ID, watch.Elapsed.TotalSeconds, audioFiles.Length, strikes.Count);

        return strikes;
    }

    private OsPath GetRemoteDownloadPath(SlskdProviderSettings settings)
    {
        OsPath remote = new(settings.DownloadPath);
        return _remotePathMappingService.RemapRemoteToLocal(settings.Host, remote);
    }

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

    private async Task CleanupSupersededAttemptsAsync(List<SlskdDownloadItem> stale, SlskdProviderSettings settings, HashSet<string> protectBasenames)
    {
        // Bounded so a hung slskd can't stall the grab; protectBasenames keeps any
        // delete that outlives the bound from touching the retry's own files.
        try
        {
            await Task.WhenAll(stale.Select(s => RemoveItemFilesAsync(s, settings, protectBasenames))).WaitAsync(TimeSpan.FromSeconds(20));
        }
        catch (TimeoutException)
        {
            _logger.Warn("Superseded cleanup of {Count} attempt(s) did not finish in time; retry files are protected", stale.Count);
        }
        catch (Exception ex)
        {
            _logger.Warn(ex, "Superseded cleanup of {Count} attempt(s) failed", stale.Count);
        }
    }

    private async Task RemoveItemFilesAsync(SlskdDownloadItem item, SlskdProviderSettings settings, HashSet<string>? protectBasenames = null)
    {
        // The merged directory view can carry another item's transfers from a shared
        // peer directory; cancelling/removing those would kill a live download.
        List<SlskdDownloadFile> files = (item.SlskdDownloadDirectory?.Files ?? [])
            .Where(f => item.OwnsAcceptedFile(f.Filename))
            .ToList();

        if (files.Count == 0 || item.Username == null)
        {
            _logger.Debug("No slskd transfers to cancel for {ItemId} (directory not populated); relying on local folder deletion", item.ID);
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
                // file.Filename is remote; only its basename exists locally, in the
                // item's resolved output folder.
                string fileName = Path.GetFileName((file.Filename ?? string.Empty).Replace('\\', '/'));

                // Never delete a basename the active retry shares in this folder.
                if (protectBasenames?.Contains(fileName) == true)
                {
                    _logger.Trace("Keeping local file {Filename} — shared with the active retry", fileName);
                }
                else
                {
                    string localFilePath = Path.Combine(
                        _remotePathMappingService
                            .RemapRemoteToLocal(settings.Host, item.GetFullFolderPath(new OsPath(settings.DownloadPath)))
                            .FullPath,
                        fileName);

                    if (_diskProvider.FileExists(localFilePath))
                    {
                        _diskProvider.DeleteFile(localFilePath);
                        _logger.Debug("Deleted local file: {Path}", localFilePath);
                    }
                    else
                    {
                        _logger.Trace("Local file not found or path not accessible, skipping deletion: {Filename}", Path.GetFileName(file.Filename));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to delete local file for {Filename}", Path.GetFileName(file.Filename));
            }

            _logger.Trace("Removed transfer {TransferId}", file.Id);
        }));
    }

    private async Task CleanStaleDirectoriesAsync(string directoryPath, Dictionary<string, long> ownedFileSizes, SlskdProviderSettings settings)
    {
        try
        {
            string localPath = _remotePathMappingService
                .RemapRemoteToLocal(settings.Host, new OsPath(Path.Combine(settings.DownloadPath, directoryPath)))
                .FullPath;

            // directoryPath is peer-controlled (slskd's advertised directory name)
            // and can carry '..' traversal. Refuse unless the resolved localPath is
            // a strict descendant of the remapped download root - otherwise a
            // crafted directory ('../../etc') would recursively delete outside it.
            string localRoot = _remotePathMappingService
                .RemapRemoteToLocal(settings.Host, new OsPath(settings.DownloadPath))
                .FullPath
                .TrimEnd('/', '\\');

            if (!IsStrictDescendantOfRoot(localPath, localRoot))
            {
                _logger.Warn("Refusing stale-directory cleanup: '{Path}' is not inside the download root '{Root}'", localPath, localRoot);
                return;
            }

            await Task.Delay(1000);

            List<SlskdUserTransfers> all = await _apiClient.GetAllTransfersAsync(settings);
            bool hasRemaining = all.SelectMany(u => u.Directories)
                .Any(d => d.Directory.Equals(directoryPath, StringComparison.OrdinalIgnoreCase));

            if (hasRemaining)
            {
                _logger.Trace("Directory {Directory} still has active downloads: skipping cleanup", directoryPath);
                return;
            }

            if (_diskProvider.FolderExists(localPath))
            {
                _logger.Debug("Removing stale directory: {Path}", localPath);
                DeleteFolderGuardedByOwnership(localPath, ownedFileSizes, directoryPath);

                string? parent = Path.GetDirectoryName(localPath);
                if (!string.IsNullOrEmpty(parent) && _diskProvider.FolderExists(parent) && _diskProvider.FolderEmpty(parent))
                {
                    _logger.Info("Removing empty parent directory: {Parent}", parent);
                    _diskProvider.DeleteFolder(parent, true);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error cleaning stale directories for path: {Directory}", directoryPath);
        }
    }

    private ReleaseInfo CreateReleaseInfoFromDirectory(string username, SlskdDownloadDirectory dir)
    {
        SlskdFolderData folderData = dir.CreateFolderData(username, _slskdItemsParser);
        SlskdSearchData searchData = new(null, null, false, false, 1, null);
        IGrouping<string, SlskdFileData> dirGroup = dir.ToSlskdFileDataList().GroupBy(_ => dir.Directory).First();
        AlbumData albumData = _slskdItemsParser.CreateAlbumData(dirGroup, searchData, folderData, null, 0);
        ReleaseInfo release = albumData.ToShareInfo();
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
