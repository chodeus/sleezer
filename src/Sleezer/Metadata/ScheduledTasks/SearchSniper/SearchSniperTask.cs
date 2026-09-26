using FluentValidation.Results;
using NLog;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Music;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Queue;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Plugin.Sleezer.Core.Utilities;

namespace NzbDrone.Plugin.Sleezer.Metadata.ScheduledTasks.SearchSniper
{
    public class SearchSniperTask : ScheduledTaskBase<SearchSniperTaskSettings>, IExecute<SearchSniperCommand>
    {
        private const int BatchSize = 100;
        private static readonly CacheService _cacheService = new();
        private readonly IAlbumService _albumService;
        private readonly IArtistService _artistService;
        private readonly IQueueService _queueService;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly IQualityProfileService _qualityProfileService;
        private readonly ICustomFormatCalculationService _formatService;
        private readonly IMetadataRepository _metadataRepository;
        private readonly SearchSniperRepositoryHelper _repositoryHelper;
        private readonly Logger _logger;

        public SearchSniperTask(
            IAlbumService albumService,
            IArtistService artistService,
            IQueueService queueService,
            IManageCommandQueue commandQueueManager,
            IQualityProfileService qualityProfileService,
            ICustomFormatCalculationService formatService,
            IMetadataRepository metadataRepository,
            IMainDatabase database,
            IEventAggregator eventAggregator,
            Logger logger)
        {
            _albumService = albumService;
            _artistService = artistService;
            _queueService = queueService;
            _commandQueueManager = commandQueueManager;
            _qualityProfileService = qualityProfileService;
            _formatService = formatService;
            _metadataRepository = metadataRepository;
            _repositoryHelper = new SearchSniperRepositoryHelper(database, eventAggregator, artistService);
            _logger = logger;
        }

        public override string Name => "Search Sniper";

        public override Type CommandType => typeof(SearchSniperCommand);

        public override ProviderMessage Message => new(
            "Automated search trigger that randomly selects albums for periodic scanning based on your search criteria. " +
            "Enable this metadata provider to start automatic searches.",
            ProviderMessageType.Info);

        // The command executor is resolved without a Definition, so its settings come from
        // the stored definition rather than whichever settings object was built last.
        private SearchSniperTaskSettings ResolveSettings() =>
            Settings ?? StoredProviderSettings.For<SearchSniperTaskSettings>(_metadataRepository.All(), nameof(SearchSniperTask));

        public override int IntervalMinutes => ResolveSettings().RefreshInterval;

        public override CommandPriority Priority => CommandPriority.Low;

        public override ValidationResult Test()
        {
            ValidationResult test = new();
            SearchSniperTaskSettings settings = ResolveSettings();
            InitializeCache(settings);

            if (settings.RequestCacheType == (int)CacheType.Permanent && !string.IsNullOrWhiteSpace(settings.CacheDirectory) && !Directory.Exists(settings.CacheDirectory))
            {
                try
                {
                    Directory.CreateDirectory(settings.CacheDirectory);
                }
                catch (Exception ex)
                {
                    test.Errors.Add(new ValidationFailure("CacheDirectory", $"Failed to create cache directory: {ex.Message}"));
                }
            }

            return test;
        }

        public void Execute(SearchSniperCommand message)
        {
            try
            {
                RunSearch(message, ResolveSettings());
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error during scheduled execution");
            }
        }

        private static void InitializeCache(SearchSniperTaskSettings settings)
        {
            _cacheService.CacheDuration = TimeSpan.FromDays(settings.CacheRetentionDays);
            _cacheService.CacheType = (CacheType)settings.RequestCacheType;
            _cacheService.CacheDirectory = settings.CacheDirectory;
        }

        private void RunSearch(SearchSniperCommand message, SearchSniperTaskSettings settings)
        {
            // Only Test() configured the shared cache before; a run must apply the saved settings itself.
            InitializeCache(settings);

            if (!settings.SearchMissing && !settings.SearchMissingTracks && !settings.SearchQualityCutoffNotMet)
            {
                _logger.Warn("No search options enabled. Please enable at least one search criteria.");
                return;
            }

            if (settings.StopWhenQueued > 0)
            {
                int queueCount = GetQueueCountByWaitOnType((WaitOnType)settings.WaitOn);
                if (queueCount >= settings.StopWhenQueued)
                {
                    message.SetCompletionMessage($"Skipping Search Sniper, queue threshold reached ({queueCount} {(WaitOnType)settings.WaitOn} items)");
                    _logger.Info("Skipping. Queue count ({0}) of {1} items reached threshold ({2})", queueCount, (WaitOnType)settings.WaitOn, settings.StopWhenQueued);
                    return;
                }
            }

            int targetCount = settings.RandomPicksPerInterval;
            HashSet<int> queuedAlbumIds = GetQueuedAlbumIds();
            int candidateTarget = Math.Min(targetCount * 10, 500);

            List<Album> eligibleAlbums = CollectEligibleAlbums(settings, queuedAlbumIds, candidateTarget);

            if (eligibleAlbums.Count == 0)
            {
                message.SetCompletionMessage("Search Sniper completed. No eligible albums found.");
                _logger.Info("No eligible albums found after filtering queued and cached albums");
                return;
            }

            List<Album> selectedAlbums = SelectRandomAlbums(eligibleAlbums, targetCount);

            foreach (Album album in selectedAlbums)
                _logger.Trace("Selected: '{0}' by {1}", album.Title, album.Artist?.Value?.Name ?? "Unknown Artist");

            CacheSelectedAlbumsAsync(selectedAlbums).GetAwaiter().GetResult();

            if (selectedAlbums.Count > 0)
            {
                _commandQueueManager.Push(new AlbumSearchCommand(selectedAlbums.ConvertAll(a => a.Id)));
                message.SetCompletionMessage($"Search Sniper completed. Queued {selectedAlbums.Count} album(s) for search");
                _logger.Info("Queued {0} album(s) for search", selectedAlbums.Count);
            }
        }

        private List<Album> CollectEligibleAlbums(SearchSniperTaskSettings settings, HashSet<int> queuedAlbumIds, int candidateTarget)
        {
            Dictionary<int, Album> eligibleAlbums = [];
            Dictionary<int, List<int>>? profileCutoffs = null;

            (int minId, int maxId) missingIdRange = (0, 0);
            (int minId, int maxId) partialIdRange = (0, 0);
            (int minId, int maxId) cutoffIdRange = (0, 0);

            if (settings.SearchMissing)
                missingIdRange = GetMissingAlbumsIdRange();

            if (settings.SearchMissingTracks)
                partialIdRange = _repositoryHelper.GetPartialAlbumsIdRange();

            if (settings.SearchQualityCutoffNotMet)
            {
                profileCutoffs = SearchSniperRepositoryHelper.BuildProfileCutoffs(_qualityProfileService.All());
                if (profileCutoffs.Count > 0)
                    cutoffIdRange = _repositoryHelper.GetCutoffUnmetAlbumsIdRange(profileCutoffs);
            }

            if (settings.SearchMissing && missingIdRange.maxId > 0 && eligibleAlbums.Count < candidateTarget)
            {
                int startId = GetRandomStartId(missingIdRange.minId, missingIdRange.maxId);
                _logger.Trace("Fetching missing albums (ID range: {0}-{1}, starting at ID: {2})...", missingIdRange.minId, missingIdRange.maxId, startId);

                CollectFromSource(
                    lastId => GetMissingAlbumsBatch(lastId),
                    eligibleAlbums, queuedAlbumIds, candidateTarget, startId, missingIdRange.minId);
            }

            if (settings.SearchMissingTracks && partialIdRange.maxId > 0 && eligibleAlbums.Count < candidateTarget)
            {
                int startId = GetRandomStartId(partialIdRange.minId, partialIdRange.maxId);
                _logger.Trace("Fetching partial albums (ID range: {0}-{1}, starting at ID: {2})...", partialIdRange.minId, partialIdRange.maxId, startId);

                CollectFromSource(
                    lastId => _repositoryHelper.GetPartialAlbumsBatch(lastId, BatchSize),
                    eligibleAlbums, queuedAlbumIds, candidateTarget, startId, partialIdRange.minId);
            }

            if (settings.SearchQualityCutoffNotMet && cutoffIdRange.maxId > 0 && eligibleAlbums.Count < candidateTarget)
            {
                int startId = GetRandomStartId(cutoffIdRange.minId, cutoffIdRange.maxId);
                _logger.Trace("Fetching cutoff unmet albums (ID range: {0}-{1}, starting at ID: {2})...", cutoffIdRange.minId, cutoffIdRange.maxId, startId);

                CollectFromSource(
                    lastId => _repositoryHelper.GetCutoffUnmetAlbumsBatch(profileCutoffs!, lastId, BatchSize),
                    eligibleAlbums, queuedAlbumIds, candidateTarget, startId, cutoffIdRange.minId);
            }

            if (settings.SearchQualityCutoffNotMet && eligibleAlbums.Count < candidateTarget)
                CollectCustomFormatCutoffUnmet(eligibleAlbums, queuedAlbumIds, candidateTarget);

            AssignArtistsToAlbums(eligibleAlbums.Values);

            _logger.Info("Collected {0} eligible album(s) for random selection", eligibleAlbums.Count);
            return [.. eligibleAlbums.Values];
        }

        private void CollectCustomFormatCutoffUnmet(Dictionary<int, Album> eligibleAlbums, HashSet<int> queuedAlbumIds, int candidateTarget)
        {
            Dictionary<int, QualityProfile> profiles = CustomFormatCutoff.Profiles(_qualityProfileService.All()).ToDictionary(p => p.Id);
            (int minId, int maxId) = _repositoryHelper.GetAlbumsWithFilesIdRange(profiles.Keys);
            if (maxId == 0)
                return;

            int startId = GetRandomStartId(minId, maxId);
            _logger.Trace("Fetching custom format cutoff unmet albums (ID range: {0}-{1}, starting at ID: {2})...", minId, maxId, startId);

            CollectFromSource(
                lastId => _repositoryHelper.GetAlbumsWithFilesBatch(profiles.Keys, lastId, BatchSize),
                eligibleAlbums, queuedAlbumIds, candidateTarget, startId, minId,
                batch => KeepCustomFormatCutoffUnmet(batch, profiles));
        }

        private List<Album> KeepCustomFormatCutoffUnmet(List<Album> batch, Dictionary<int, QualityProfile> profiles)
        {
            Dictionary<int, TrackFile> firstFiles = _repositoryHelper.GetFirstTrackFiles(batch.Select(a => a.Id));

            return batch.Where(a => a.Artist?.Value is { } artist
                                    && firstFiles.TryGetValue(a.Id, out TrackFile? file)
                                    && profiles.TryGetValue(artist.QualityProfileId, out QualityProfile? profile)
                                    && CustomFormatCutoff.IsUnmet(profile, _formatService.ParseCustomFormat(file, artist)))
                        .ToList();
        }

        private static int GetRandomStartId(int minId, int maxId)
        {
            if (maxId <= minId)
                return minId;
            return Random.Shared.Next(minId, maxId + 1);
        }

        private void CollectFromSource(
            Func<int, List<Album>> fetchBatch,
            Dictionary<int, Album> eligibleAlbums,
            HashSet<int> queuedAlbumIds,
            int candidateTarget,
            int startId,
            int minId,
            Func<List<Album>, List<Album>>? keep = null)
        {
            int lastId = startId - 1;
            bool hasWrapped = false;
            int maxIterations = 100;
            int iterations = 0;

            while (eligibleAlbums.Count < candidateTarget && iterations++ < maxIterations)
            {
                List<Album> batch = fetchBatch(lastId);

                if (batch.Count == 0)
                {
                    if (!hasWrapped && lastId >= startId)
                    {
                        hasWrapped = true;
                        lastId = minId - 1;
                        continue;
                    }
                    break;
                }

                // Paging follows the unfiltered batch; keep only narrows what is collected.
                foreach (Album album in keep?.Invoke(batch) ?? batch)
                {
                    if (hasWrapped && album.Id >= startId)
                        return;

                    if (eligibleAlbums.Count >= candidateTarget)
                        return;

                    if (queuedAlbumIds.Contains(album.Id))
                        continue;

                    if (eligibleAlbums.ContainsKey(album.Id))
                        continue;

                    if (IsAlbumCached(album))
                        continue;

                    eligibleAlbums[album.Id] = album;
                }

                lastId = batch[^1].Id;

                if (hasWrapped && lastId >= startId)
                    return;

                if (!hasWrapped && batch.Count < BatchSize)
                {
                    hasWrapped = true;
                    lastId = minId - 1;
                }
            }
        }

        private HashSet<int> GetQueuedAlbumIds() =>
            _queueService.GetQueue()
                .Where(q => q.Album is not null)
                .Select(q => q.Album!.Id)
                .ToHashSet();

        private static bool IsAlbumCached(Album album)
        {
            string cacheKey = GenerateCacheKey(album);
            return _cacheService.GetAsync<bool>(cacheKey).GetAwaiter().GetResult();
        }

        private void AssignArtistsToAlbums(IEnumerable<Album> albums)
        {
            List<Album> albumsNeedingArtist = albums.Where(a => a.Artist?.Value == null).ToList();
            if (albumsNeedingArtist.Count == 0)
                return;

            HashSet<int> artistIds = albumsNeedingArtist
                .Where(a => a.ArtistMetadataId > 0)
                .Select(a => a.ArtistMetadataId)
                .ToHashSet();

            if (artistIds.Count == 0)
                return;

            Dictionary<int, Artist> artistsByMetadataId = [];
            foreach (Artist artist in _artistService.GetAllArtists())
            {
                if (artistIds.Contains(artist.ArtistMetadataId))
                {
                    artistsByMetadataId[artist.ArtistMetadataId] = artist;
                    if (artistsByMetadataId.Count == artistIds.Count)
                        break;
                }
            }

            foreach (Album album in albumsNeedingArtist)
            {
                if (artistsByMetadataId.TryGetValue(album.ArtistMetadataId, out Artist? artist))
                    album.Artist = new LazyLoaded<Artist>(artist);
            }
        }

        private List<Album> GetMissingAlbumsBatch(int lastId)
        {
            PagingSpec<Album> pagingSpec = new()
            {
                Page = 1,
                PageSize = BatchSize,
                SortDirection = SortDirection.Ascending,
                SortKey = "Id"
            };

            pagingSpec.FilterExpressions.Add(v => v.Id > lastId);
            pagingSpec.FilterExpressions.Add(v => v.Monitored == true && v.Artist.Value.Monitored == true);

            return _albumService.AlbumsWithoutFiles(pagingSpec).Records;
        }

        private (int minId, int maxId) GetMissingAlbumsIdRange()
        {
            try
            {
                PagingSpec<Album> minSpec = new()
                {
                    Page = 1,
                    PageSize = 1,
                    SortDirection = SortDirection.Ascending,
                    SortKey = "Id"
                };
                minSpec.FilterExpressions.Add(v => v.Monitored == true && v.Artist.Value.Monitored == true);
                List<Album> minResult = _albumService.AlbumsWithoutFiles(minSpec).Records;

                if (minResult.Count == 0)
                    return (0, 0);

                PagingSpec<Album> maxSpec = new()
                {
                    Page = 1,
                    PageSize = 1,
                    SortDirection = SortDirection.Descending,
                    SortKey = "Id"
                };
                maxSpec.FilterExpressions.Add(v => v.Monitored == true && v.Artist.Value.Monitored == true);
                List<Album> maxResult = _albumService.AlbumsWithoutFiles(maxSpec).Records;

                return (minResult[0].Id, maxResult.Count > 0 ? maxResult[0].Id : minResult[0].Id);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error getting missing albums ID range");
                return (0, 0);
            }
        }

        private int GetQueueCountByWaitOnType(WaitOnType waitOnType)
        {
            List<Queue> queue = _queueService.GetQueue();

            return waitOnType switch
            {
                WaitOnType.Queued => queue.Count(x => x.Status == "Queued"),
                WaitOnType.Downloading => queue.Count(x => x.Status == "Downloading"),
                WaitOnType.Warning => queue.Count(x => x.Status == "Warning"),
                WaitOnType.QueuedAndDownloading => queue.Count(x => x.Status == "Queued" || x.Status == "Downloading"),
                WaitOnType.All => queue.Count(x => x.Status != "Completed" && x.Status != "Failed"),
                _ => 0
            };
        }

        private static async Task CacheSelectedAlbumsAsync(List<Album> albums)
        {
            foreach (Album album in albums)
            {
                string cacheKey = GenerateCacheKey(album);
                await _cacheService.SetAsync(cacheKey, true);
            }
        }

        private static List<Album> SelectRandomAlbums(List<Album> albums, int count)
        {
            int pickCount = Math.Min(count, albums.Count);
            return albums.OrderBy(_ => Random.Shared.Next()).Take(pickCount).ToList();
        }

        private static string GenerateCacheKey(Album album) =>
            $"SearchSniper:{album.Artist?.Value?.Name ?? "Unknown"}:{album.Id}";
    }
}
