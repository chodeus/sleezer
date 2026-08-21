using FuzzySharp;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.History;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Queue;
using System.Text.Json;
using NzbDrone.Plugin.Sleezer.Core.Model;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;
using System.Net;
using System.Text.Json.Nodes;

namespace NzbDrone.Plugin.Sleezer.Indexers.Soulseek
{
    public class SlskdIndexerParser : IParseIndexerResponse, IHandle<AlbumGrabbedEvent>, IHandle<ApplicationShutdownRequested>
    {
        private readonly SlskdIndexer _indexer;
        private readonly Logger _logger;
        private readonly Lazy<IIndexerFactory> _indexerFactory;
        private readonly IHttpClient _httpClient;
        private readonly ISlskdItemsParser _itemsParser;
        private readonly IHistoryService _historyService;
        private readonly IDownloadHistoryService _downloadHistoryService;
        private readonly IQueueService _queueService;
        private readonly ISlskdCorruptUserTracker _corruptUserTracker;

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _interactiveResults = new();
        private static readonly Dictionary<string, (HashSet<string> IgnoredUsers, long LastFileSize)> _ignoreListCache = new();
        private readonly object _rateLimitLock = new();
        private DateTime _rateLimitCacheTimestamp = DateTime.MinValue;
        private HashSet<string> _rateLimitedUsersCache = new(StringComparer.OrdinalIgnoreCase);

        // Static because Lidarr resolves indexers (and thus parsers) transiently
        // per fetch — instance caches would re-walk 24h of history on every
        // search command.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (DateTime Timestamp, HashSet<string> Ids, Dictionary<string, int> Users)> _failureStateCache = new();

        private SlskdSettings Settings => _indexer.Settings;

        public SlskdIndexerParser(SlskdIndexer indexer, Lazy<IIndexerFactory> indexerFactory, IHttpClient httpClient, ISlskdItemsParser itemsParser, IHistoryService historyService, IDownloadHistoryService downloadHistoryService, IQueueService queueService, ISlskdCorruptUserTracker corruptUserTracker)
        {
            _indexer = indexer;
            _indexerFactory = indexerFactory;
            _logger = NzbDroneLogger.GetLogger(this);
            _httpClient = httpClient;
            _itemsParser = itemsParser;
            _historyService = historyService;
            _downloadHistoryService = downloadHistoryService;
            _queueService = queueService;
            _corruptUserTracker = corruptUserTracker;
        }

        public IList<ReleaseInfo> ParseResponse(IndexerResponse indexerResponse)
        {
            List<AlbumData> albumDatas = [];
            string? searchIdForCleanup = null;
            bool delayRemoval = false;
            try
            {
                SlskdSearchResponse? searchResponse = JsonSerializer.Deserialize<SlskdSearchResponse>(indexerResponse.Content, IndexerParserHelper.StandardJsonOptions);

                if (searchResponse == null)
                {
                    var snippet = indexerResponse.Content?.Length > 256 ? indexerResponse.Content[..256] + "…" : indexerResponse.Content;
                    _logger.Error("Failed to deserialize slskd search response — body snippet: {Body}", snippet ?? "<empty>");
                    return [];
                }

                searchIdForCleanup = searchResponse.Id;
                SlskdSearchData searchTextData = SlskdSearchData.FromJson(indexerResponse.HttpRequest.ContentSummary);
                HashSet<string>? ignoredUsers = GetIgnoredUsers(Settings.IgnoreListPath);
                HashSet<string> rateLimitedUsers = GetRateLimitedUsers();
                (HashSet<string> recentlyFailedIds, Dictionary<string, int> recentlyFailedUsers) = GetRecentFailureState();

                int totalResponses = searchResponse.Responses?.Count() ?? 0;
                int droppedIgnored = 0;
                int droppedRateLimited = 0;
                int droppedRecentlyFailed = 0;
                int droppedUnmatched = 0;
                int plucked = 0;
                int unmatched = 0;

                foreach (SlskdFolderData response in searchResponse.Responses ?? Enumerable.Empty<SlskdFolderData>())
                {
                    if (ignoredUsers?.Contains(response.Username) == true)
                    {
                        droppedIgnored++;
                        continue;
                    }
                    if (rateLimitedUsers.Contains(response.Username))
                    {
                        droppedRateLimited++;
                        continue;
                    }

                    IEnumerable<SlskdFileData> filteredFiles = SlskdFileData.GetFilteredFiles(response.Files, Settings.OnlyAudioFiles, Settings.IncludeFileExtensions);

                    foreach (IGrouping<string, SlskdFileData> directoryGroup in filteredFiles.GroupBy(f => SlskdTextProcessor.GetMergedDirectoryKey(f.Filename)))
                    {
                        if (string.IsNullOrEmpty(directoryGroup.Key))
                            continue;

                        SlskdFolderData folderData = _itemsParser.ParseFolderName(directoryGroup.Key) with
                        {
                            Username = response.Username,
                            HasFreeUploadSlot = response.HasFreeUploadSlot,
                            UploadSpeed = response.UploadSpeed,
                            LockedFileCount = response.LockedFileCount,
                            LockedFiles = response.LockedFiles,
                            QueueLength = response.QueueLength,
                            Token = response.Token,
                            FileCount = response.FileCount,
                            RecentUserFailures = recentlyFailedUsers.GetValueOrDefault(response.Username)
                        };

                        IGrouping<string, SlskdFileData> finalGroup = directoryGroup;
                        if (searchTextData.ExpandDirectory)
                        {
                            IGrouping<string, SlskdFileData>? expandedGroup = TryExpandDirectory(searchTextData, directoryGroup, folderData);
                            if (expandedGroup != null)
                                finalGroup = expandedGroup;
                        }

                        if (searchTextData.MinimumFiles > 0 || searchTextData.MaximumFiles.HasValue)
                        {
                            bool filterActive = (TrackCountFilterType)Settings.TrackCountFilter != TrackCountFilterType.Disabled;
                            int fileCount = filterActive
                                ? finalGroup.Count(f => AudioFormatHelper.GetAudioCodecFromExtension(f.Extension ?? Path.GetExtension(f.Filename) ?? "") != AudioFormat.Unknown)
                                : finalGroup.Count();

                            if (fileCount < searchTextData.MinimumFiles)
                            {
                                _logger.Trace("Filtered (too few): {Directory} ({Count}/{Min} {Kind})", directoryGroup.Key, fileCount, searchTextData.MinimumFiles, filterActive ? "audio tracks" : "files");
                                continue;
                            }

                            if (searchTextData.MaximumFiles.HasValue && fileCount > searchTextData.MaximumFiles.Value)
                            {
                                _logger.Trace("Filtered (too many): {Directory} ({Count}/{Max} {Kind})", directoryGroup.Key, fileCount, searchTextData.MaximumFiles, filterActive ? "audio tracks" : "files");
                                continue;
                            }
                        }

                        AlbumData albumData = _itemsParser.CreateAlbumData(finalGroup, searchTextData, folderData, Settings, searchTextData.TrackCount);

                        // Carries the search this release came from; the display link
                        // points at the peer, so grab cleanup matches on this instead.
                        albumData.CommentUrl = SlskdUrls.Search(Settings, searchResponse.Id);
                        List<SlskdFileData> downloadFiles = JsonSerializer.Deserialize<List<SlskdFileData>>(albumData.CustomString, IndexerParserHelper.StandardJsonOptions) ?? [];

                        // Per-directory single/EP decisions log at Debug (too many to surface
                        // individually), so tally them for the Info summary below.
                        int groupAudioCount = finalGroup.Count(f => AudioFormatHelper.GetAudioCodecFromExtension(f.Extension ?? Path.GetExtension(f.Filename) ?? "") != AudioFormat.Unknown);
                        if (downloadFiles.Count > 0 && downloadFiles.Count < groupAudioCount)
                            plucked++;
                        if (!albumData.MatchedSearchCriteria)
                        {
                            // Unmatched releases: kept for interactive searches,
                            // dropped for automatic ones (else they can win the grab).
                            if (!searchTextData.Interactive)
                            {
                                droppedUnmatched++;
                                _logger.Trace("Filtered (unmatched, automatic search): {Directory}", directoryGroup.Key);
                                continue;
                            }

                            unmatched++;
                        }

                        // Skip recently-failed sources (Lidarr's blocklist can't — no
                        // protocol on its Soulseek rows). Hash the ACTUAL download set
                        // (CustomString may be a plucked subset), not finalGroup, so the
                        // id matches the real downloadId. Interactive = deliberate re-grab.
                        if (!searchTextData.Interactive)
                        {
                            string prospectiveId = SlskdDownloadItem.GetStableMD5Id(downloadFiles.Select(f => f.Filename));
                            if (recentlyFailedIds.Contains(prospectiveId))
                            {
                                droppedRecentlyFailed++;
                                _logger.Trace("Filtered (failed within 24h): {Directory}", directoryGroup.Key);
                                continue;
                            }
                        }

                        albumDatas.Add(albumData);
                    }
                }

                _logger.Info("Slskd parse: {Total} response(s), {Albums} album(s) emitted ({Plucked} narrowed to matched track(s), {Unmatched} unmatched), dropped {Ignored} ignored-user / {RateLimited} rate-limited / {RecentlyFailed} recently-failed / {UnmatchedDropped} unmatched",
                    totalResponses, albumDatas.Count, plucked, unmatched, droppedIgnored, droppedRateLimited, droppedRecentlyFailed, droppedUnmatched);

                delayRemoval = albumDatas.Count != 0 && searchTextData.Interactive;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to parse Slskd search response.");
            }
            finally
            {
                // The DELETE must run even when parsing throws, or slskd's
                // search list grows unboundedly on transient parse failures.
                if (searchIdForCleanup != null)
                    RemoveSearch(searchIdForCleanup, delayRemoval);
            }

            return albumDatas.OrderByDescending(x => x.Priotity).Select(a => (ReleaseInfo)a.ToShareInfo()).ToList();
        }

        private IGrouping<string, SlskdFileData>? TryExpandDirectory(SlskdSearchData searchTextData, IGrouping<string, SlskdFileData> directoryGroup, SlskdFolderData folderData)
        {
            if (string.IsNullOrEmpty(searchTextData.Artist) || string.IsNullOrEmpty(searchTextData.Album))
                return null;

            int artistRatio = Fuzz.PartialRatio(folderData.Artist, searchTextData.Artist);
            int albumRatio = Fuzz.PartialRatio(folderData.Album, searchTextData.Album);
            bool artistMatch = artistRatio > 85;
            bool albumMatch = albumRatio > 85;

            if (!artistMatch || !albumMatch)
            {
                _logger.Trace("Skip expand for {Username}:{Directory} — artist ratio {ArtistRatio}, album ratio {AlbumRatio} (need >85)",
                    folderData.Username, directoryGroup.Key, artistRatio, albumRatio);
                return null;
            }

            SlskdFileData? originalTrack = directoryGroup.FirstOrDefault(x => AudioFormatHelper.GetAudioCodecFromExtension(x.Extension?.ToLowerInvariant() ?? Path.GetExtension(x.Filename) ?? "") != AudioFormat.Unknown);

            if (originalTrack == null)
                return null;

            _logger.Trace("Expanding directory for: {Username}:{Directory}", folderData.Username, directoryGroup.Key);

            SlskdRequestGenerator? requestGenerator = _indexer.GetExtendedRequestGenerator() as SlskdRequestGenerator;
            IGrouping<string, SlskdFileData>? expandedGroup = requestGenerator?.ExpandDirectory(folderData.Username, directoryGroup.Key, originalTrack).GetAwaiter().GetResult();

            if (expandedGroup != null)
            {
                _logger.Debug("Successfully expanded directory to {Count} files", expandedGroup.Count());
                return expandedGroup;
            }
            else
            {
                _logger.Warn("Failed to expand directory for {Username}:{Directory}", folderData.Username, directoryGroup.Key);
            }
            return null;
        }

        public void RemoveSearch(string searchId, bool delay = false)
        {
            Task.Run(async () =>
            {
                try
                {
                    if (delay)
                    {
                        int? indexerId = _indexer.Definition?.Id;
                        if (indexerId == null)
                            return;

                        string? staleId = null;
                        _interactiveResults.AddOrUpdate(
                            indexerId.Value,
                            searchId,
                            (_, previous) => { staleId = previous; return searchId; });
                        if (staleId != null)
                            searchId = staleId;
                        else return;
                    }
                    await ExecuteRemovalAsync(Settings, searchId);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to remove slskd search with ID: {SearchId}", searchId);
                }
            });
        }

        public void Handle(AlbumGrabbedEvent message)
        {
            if (!_interactiveResults.TryGetValue(message.Album.Release.IndexerId, out string? selectedId) ||
                !SlskdUrls.IsFromSearch(message.Album.Release.CommentUrl, selectedId))
                return;
            ExecuteRemovalAsync((SlskdSettings)_indexerFactory.Value.Get(message.Album.Release.IndexerId).Settings, selectedId).GetAwaiter().GetResult();
            _interactiveResults.TryRemove(message.Album.Release.IndexerId, out _);
        }

        public void Handle(ApplicationShutdownRequested message)
        {
            foreach (int indexerId in _interactiveResults.Keys.ToList())
            {
                if (_interactiveResults.TryRemove(indexerId, out string? selectedId))
                {
                    ExecuteRemovalAsync((SlskdSettings)_indexerFactory.Value.Get(indexerId).Settings, selectedId).GetAwaiter().GetResult();
                }
            }
        }

        public static void InvalidIgnoreCache(string path) => _ignoreListCache.Remove(path);

        private HashSet<string> GetRateLimitedUsers()
        {
            IReadOnlySet<string> corruptBanned = _corruptUserTracker.GetBannedUsers();

            if (Settings.MaxGrabsPerUser <= 0 && Settings.MaxQueuedPerUser <= 0 && corruptBanned.Count == 0)
                return [];

            lock (_rateLimitLock)
            {
                if (DateTime.UtcNow - _rateLimitCacheTimestamp < TimeSpan.FromSeconds(15))
                    return _rateLimitedUsersCache;

                HashSet<string> blocked = new(StringComparer.OrdinalIgnoreCase);

                if (Settings.MaxGrabsPerUser > 0)
                    foreach ((string? user, int count) in GetGrabCounts())
                        if (count >= Settings.MaxGrabsPerUser)
                            blocked.Add(user);

                if (Settings.MaxQueuedPerUser > 0)
                    foreach ((string? user, int count) in GetQueuedCounts())
                        if (count >= Settings.MaxQueuedPerUser)
                            blocked.Add(user);

                foreach (string banned in corruptBanned)
                    blocked.Add(banned);

                _rateLimitedUsersCache = blocked;
                _rateLimitCacheTimestamp = DateTime.UtcNow;
                return blocked;
            }
        }

        private (HashSet<string> FailedIds, Dictionary<string, int> FailedUsers) GetRecentFailureState()
        {
            int cacheKey = _indexer.Definition?.Id ?? 0;
            if (_failureStateCache.TryGetValue(cacheKey, out (DateTime Timestamp, HashSet<string> Ids, Dictionary<string, int> Users) cached) &&
                DateTime.UtcNow - cached.Timestamp < TimeSpan.FromSeconds(15))
            {
                return (cached.Ids, cached.Users);
            }

            // Lookback is 2x the max backoff tier: with a 24h lookback the
            // escalated count decays as older failures age out and a dead
            // share resurfaces ~18h early. A multi-album failure writes one
            // row per album sharing the DownloadId — dedupe first.
            Dictionary<string, DateTime> rowsById = new(StringComparer.OrdinalIgnoreCase);
            foreach (EntityHistory history in _historyService.Since(DateTime.UtcNow.AddHours(-48), EntityHistoryEventType.DownloadFailed))
            {
                if (string.IsNullOrWhiteSpace(history.DownloadId))
                    continue;

                DateTime seen = rowsById.GetValueOrDefault(history.DownloadId);
                if (history.Date > seen)
                    rowsById[history.DownloadId] = history.Date;
            }

            // Group by base release id: retry grabs record their failure under
            // the -rN suffix, and the backoff escalates with the per-release
            // attempt count (1h → 6h → 24h). Users are charged per bad
            // RELEASE, not per retry of the same one.
            Dictionary<string, (int Count, DateTime Last)> perRelease = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> perUser = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> chargedUserReleases = new(StringComparer.OrdinalIgnoreCase);
            int? indexerId = _indexer.Definition?.Id;
            foreach ((string downloadId, DateTime date) in rowsById)
            {
                // Same scoping as GetGrabCounts: a torrent failure's
                // DownloadUrl would otherwise contribute a junk "username".
                DownloadHistory? grab = _downloadHistoryService.GetLatestGrab(downloadId);
                if (grab == null || !string.Equals(grab.Protocol, nameof(SoulseekDownloadProtocol), StringComparison.OrdinalIgnoreCase))
                    continue;
                if (indexerId.HasValue && grab.IndexerId != indexerId.Value)
                    continue;

                string baseId = SlskdDownloadItem.StripRetrySuffix(downloadId);
                (int count, DateTime last) = perRelease.GetValueOrDefault(baseId);
                perRelease[baseId] = (count + 1, date > last ? date : last);

                string? username = ExtractUsernameFromUrl(grab.Release?.DownloadUrl);
                if (username != null && chargedUserReleases.Add(username + "|" + baseId))
                    perUser[username] = perUser.GetValueOrDefault(username) + 1;
            }

            HashSet<string> failed = new(StringComparer.OrdinalIgnoreCase);
            foreach ((string baseId, (int count, DateTime last)) in perRelease)
            {
                if (DateTime.UtcNow - last < SlskdDownloadItem.RetryBackoffWindow(count))
                    failed.Add(baseId);
            }

            _failureStateCache[cacheKey] = (DateTime.UtcNow, failed, perUser);
            return (failed, perUser);
        }

        private Dictionary<string, int> GetGrabCounts()
        {
            DateTime since = (GrabLimitIntervalType)Settings.GrabLimitInterval switch
            {
                GrabLimitIntervalType.Hour => DateTime.UtcNow.AddHours(-1),
                GrabLimitIntervalType.Week => DateTime.UtcNow.AddDays(-7),
                _ => DateTime.UtcNow.Date
            };

            int? indexerId = _indexer.Definition?.Id;
            Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);

            IEnumerable<string> downloadIds = _historyService.Since(since, EntityHistoryEventType.Grabbed)
                .Select(h => h.DownloadId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (string downloadId in downloadIds)
            {
                DownloadHistory? grab = _downloadHistoryService.GetLatestGrab(downloadId);
                if (grab == null)
                    continue;

                if (!string.Equals(grab.Protocol, nameof(SoulseekDownloadProtocol), StringComparison.OrdinalIgnoreCase))
                    continue;

                if (indexerId.HasValue && grab.IndexerId != indexerId.Value)
                    continue;

                string? username = ExtractUsernameFromUrl(grab.Release?.DownloadUrl);
                if (username != null)
                    counts[username] = counts.GetValueOrDefault(username) + 1;
            }

            return counts;
        }

        private Dictionary<string, int> GetQueuedCounts()
        {
            string? indexerName = _indexer.Definition?.Name;
            Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

            foreach (Queue item in _queueService.GetQueue())
            {
                if (!string.Equals(item.Protocol, nameof(SoulseekDownloadProtocol), StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.IsNullOrWhiteSpace(indexerName) && !string.Equals(item.Indexer, indexerName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (string.IsNullOrWhiteSpace(item.DownloadId) || !seen.Add(item.DownloadId))
                    continue;

                DownloadHistory? grab = _downloadHistoryService.GetLatestGrab(item.DownloadId);
                if (grab == null)
                    continue;

                string? username = ExtractUsernameFromUrl(grab.Release?.DownloadUrl);
                if (username != null)
                    counts[username] = counts.GetValueOrDefault(username) + 1;
            }

            return counts;
        }

        private static string? ExtractUsernameFromUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return null;

            int lastSlash = url.LastIndexOf('/');
            if (lastSlash < 0 || lastSlash >= url.Length - 1)
                return null;

            return Uri.UnescapeDataString(url[(lastSlash + 1)..]);
        }

        private async Task ExecuteRemovalAsync(SlskdSettings settings, string searchId)
        {
            try
            {
                // Never DELETE a search slskd is still running — that races its
                // finalize write and loses the results (the exact failure the
                // cancel path guards). Confirm terminal (or already-gone 404)
                // first; if it is still InProgress, leave the record for a
                // later pass rather than delete it in flight.
                HttpRequest statusRequest = new HttpRequestBuilder($"{settings.BaseUrl}/api/v0/searches/{searchId}")
                    .SetHeader("X-API-KEY", settings.ApiKey)
                    .Build();
                statusRequest.SuppressHttpError = true;
                HttpResponse statusResponse = await _httpClient.ExecuteAsync(statusRequest);

                if (statusResponse.StatusCode == HttpStatusCode.NotFound)
                    return;

                if (statusResponse.StatusCode == HttpStatusCode.OK)
                {
                    string? state = JsonSerializer.Deserialize<JsonNode>(statusResponse.Content)?["state"]?.GetValue<string>();
                    if (state != null && state.StartsWith("InProgress", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.Debug("Slskd search {SearchId} still in progress; deferring removal", searchId);
                        return;
                    }
                }

                HttpRequest request = new HttpRequestBuilder($"{settings.BaseUrl}/api/v0/searches/{searchId}")
                    .SetHeader("X-API-KEY", settings.ApiKey)
                    .Build();
                request.Method = HttpMethod.Delete;
                await _httpClient.ExecuteAsync(request);
            }
            catch (HttpException ex)
            {
                _logger.Error(ex, "Failed to remove slskd search with ID: {SearchId}", searchId);
            }
        }

        private HashSet<string>? GetIgnoredUsers(string? ignoreListPath)
        {
            if (string.IsNullOrWhiteSpace(ignoreListPath) || !File.Exists(ignoreListPath))
                return null;

            try
            {
                FileInfo fileInfo = new(ignoreListPath);
                long fileSize = fileInfo.Length;

                if (_ignoreListCache.TryGetValue(ignoreListPath, out (HashSet<string> IgnoredUsers, long LastFileSize) cached) && cached.LastFileSize == fileSize)
                {
                    _logger.Trace("Using cached ignore list from: {Path} with {Count} users", ignoreListPath, cached.IgnoredUsers.Count);
                    return cached.IgnoredUsers;
                }
                HashSet<string> ignoredUsers = SlskdTextProcessor.ParseListContent(File.ReadAllText(ignoreListPath));
                _ignoreListCache[ignoreListPath] = (ignoredUsers, fileSize);
                _logger.Trace("Loaded ignore list with {Count} users from: {Path}", ignoredUsers.Count, ignoreListPath);
                return ignoredUsers;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to load ignore list from: {Path}", ignoreListPath);
                return null;
            }
        }
    }
}