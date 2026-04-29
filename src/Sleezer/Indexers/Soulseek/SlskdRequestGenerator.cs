using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Exceptions;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Music;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using NzbDrone.Plugin.Sleezer.Core.Replacements;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek.Search.Core;

namespace NzbDrone.Plugin.Sleezer.Indexers.Soulseek
{
    internal class SlskdRequestGenerator : IIndexerRequestGenerator<LazyIndexerPageableRequest>
    {
        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

        // slskd's POST /api/v0/searches is single-concurrency per server: starting a second search while one is
        // still running yields 429 "Only one concurrent operation is permitted". Lidarr fans out album searches
        // in parallel (e.g. when a bulk delete triggers 50 missing-album searches), so we serialize the entire
        // create-and-poll lifecycle here. Keyed per BaseUrl so multiple slskd instances each get their own gate.
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _searchGates = new();

        private readonly SlskdIndexer _indexer;
        private readonly Logger _logger;
        private readonly IHttpClient _client;
        private readonly ISlskdSearchChain _searchPipeline;
        private readonly HashSet<string> _processedSearches = new(StringComparer.OrdinalIgnoreCase);

        private SlskdSettings Settings => _indexer.Settings;

        public SlskdRequestGenerator(SlskdIndexer indexer, ISlskdSearchChain searchPipeline, IHttpClient client)
        {
            _indexer = indexer;
            _client = client;
            _searchPipeline = searchPipeline;
            _logger = NzbDroneLogger.GetLogger(this);
        }

        public IndexerPageableRequestChain<LazyIndexerPageableRequest> GetRecentRequests() => new LazyIndexerPageableRequestChain(Settings.MinimumResults);

        public IndexerPageableRequestChain<LazyIndexerPageableRequest> GetSearchRequests(AlbumSearchCriteria searchCriteria)
        {
            _logger.Trace("Setting up lazy search for album: {Album} by artist: {Artist}", searchCriteria.AlbumQuery, searchCriteria.ArtistQuery);

            Album? album = searchCriteria.Albums.FirstOrDefault();
            List<AlbumRelease>? albumReleases = album?.AlbumReleases?.Value;
            AlbumRelease? monitoredRelease = albumReleases?.FirstOrDefault(r => r.Monitored);
            int trackCount = monitoredRelease?.TrackCount
                ?? (albumReleases?.Any() == true ? albumReleases.Min(x => x.TrackCount) : 0);
            List<string> tracks = (monitoredRelease ?? albumReleases?.FirstOrDefault(x => x.Tracks?.Value is { Count: > 0 }))
                ?.Tracks?.Value?.Where(x => !string.IsNullOrEmpty(x.Title)).Select(x => x.Title).ToList() ?? [];

            _processedSearches.Clear();

            SearchContext context = new(
                Artist: searchCriteria.ArtistQuery,
                Album: searchCriteria.ArtistQuery != searchCriteria.AlbumQuery ? searchCriteria.AlbumQuery : null,
                Year: searchCriteria.AlbumYear.ToString(),
                PrimaryType: GetPrimaryAlbumType(album?.AlbumType),
                Interactive: searchCriteria.InteractiveSearch,
                TrackCount: trackCount,
                Aliases: searchCriteria.Artist?.Metadata.Value.Aliases ?? [],
                Tracks: tracks,
                Settings: Settings,
                ProcessedSearches: _processedSearches,
                SearchCriteria: searchCriteria);

            return _searchPipeline.BuildChain(context, ExecuteSearch);
        }

        public IndexerPageableRequestChain<LazyIndexerPageableRequest> GetSearchRequests(ArtistSearchCriteria searchCriteria)
        {
            _logger.Debug("Setting up lazy search for artist: {Artist}", searchCriteria.CleanArtistQuery);

            Album? album = searchCriteria.Albums.FirstOrDefault();
            List<AlbumRelease>? albumReleases = album?.AlbumReleases?.Value;
            AlbumRelease? monitoredRelease = albumReleases?.FirstOrDefault(r => r.Monitored);
            int trackCount = monitoredRelease?.TrackCount
                ?? (albumReleases?.Any() == true ? albumReleases.Min(x => x.TrackCount) : 0);
            List<string> tracks = (monitoredRelease ?? albumReleases?.FirstOrDefault(x => x.Tracks?.Value is { Count: > 0 }))
                ?.Tracks?.Value?.Where(x => !string.IsNullOrEmpty(x.Title)).Select(x => x.Title).ToList() ?? [];

            _processedSearches.Clear();

            SearchContext context = new(
                Artist: searchCriteria.CleanArtistQuery,
                Album: null,
                Year: null,
                PrimaryType: GetPrimaryAlbumType(album?.AlbumType),
                Interactive: searchCriteria.InteractiveSearch,
                TrackCount: trackCount,
                Aliases: searchCriteria.Artist?.Metadata.Value.Aliases ?? [],
                Tracks: tracks,
                Settings: Settings,
                ProcessedSearches: _processedSearches,
                SearchCriteria: searchCriteria);

            return _searchPipeline.BuildChain(context, ExecuteSearch);
        }

        private IEnumerable<IndexerRequest> ExecuteSearch(SearchQuery query)
        {
            string? searchText = query.SearchText ?? SlskdTextProcessor.BuildSearchText(query.Artist, query.Album);

            if (string.IsNullOrWhiteSpace(searchText))
                return [];

            try
            {
                IndexerRequest? request = GetRequestsAsync(query, searchText).GetAwaiter().GetResult();
                if (request != null)
                {
                    _logger.Trace("Successfully generated request for search: {SearchText}", searchText);
                    return [request];
                }
                else
                {
                    _logger.Trace("GetRequestsAsync returned null for search: {SearchText}", searchText);
                }
            }
            catch (RequestLimitReachedException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error executing search: {SearchText}", searchText);
            }

            return [];
        }

        private async Task<IndexerRequest?> GetRequestsAsync(SearchQuery query, string searchText)
        {
            try
            {
                _logger.Debug("Search: {SearchText}", searchText);

                dynamic searchData = CreateSearchData(searchText);
                string searchId = searchData.Id;
                dynamic searchRequest = CreateSearchRequest(searchData);

                await ExecuteSearchAsync(searchRequest, searchId);

                dynamic request = CreateResultRequest(searchId, query);
                return new IndexerRequest(request);
            }
            catch (HttpException ex) when (ex.Response.StatusCode == HttpStatusCode.Conflict)
            {
                throw new RequestLimitReachedException(
                    "Soulseek client is not connected (temporarily banned or disconnected). Indexer disabled.",
                    TimeSpan.FromMinutes(15));
            }
            catch (HttpException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.Warn(ex, "Search request failed for: {SearchText}", searchText);
                return null;
            }
            catch (SearchGateTimeoutException)
            {
                // Already logged at Warn inside ExecuteSearchAsync. Skip this one so Lidarr re-queues
                // the album later instead of holding a search-task slot for the full timeout.
                return null;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error generating search request for: {SearchText}", searchText);
                return null;
            }
        }

        private sealed class SearchGateTimeoutException : Exception
        {
            public SearchGateTimeoutException(string message) : base(message) { }
        }

        private dynamic CreateSearchData(string searchText) => new
        {
            Id = Guid.NewGuid().ToString(),
            Settings.FileLimit,
            FilterResponses = true,
            Settings.MaximumPeerQueueLength,
            Settings.MinimumPeerUploadSpeed,
            Settings.MinimumResponseFileCount,
            Settings.ResponseLimit,
            SearchText = searchText,
            SearchTimeout = (int)(Settings.TimeoutInSeconds * 1000),
        };

        private HttpRequest CreateSearchRequest(dynamic searchData)
        {
            HttpRequest searchRequest = new HttpRequestBuilder($"{Settings.BaseUrl}/api/v0/searches")
                .SetHeader("X-API-KEY", Settings.ApiKey)
                .SetHeader("Content-Type", "application/json")
                .Post()
                .Build();

            searchRequest.SetContent(JsonSerializer.Serialize(searchData));
            return searchRequest;
        }

        private async Task ExecuteSearchAsync(HttpRequest searchRequest, string searchId)
        {
            // Cap how long a search will sit in the queue waiting for slskd to free up. 2× the per-search
            // timeout balances "let bulk deletes drain at slskd's pace" against "don't tie up Lidarr's
            // search-task slot for an album that can be retried later".
            TimeSpan acquireCap = TimeSpan.FromSeconds(Math.Max(60, Settings.TimeoutInSeconds * 2));
            SemaphoreSlim gate = _searchGates.GetOrAdd(Settings.BaseUrl ?? string.Empty, _ => new SemaphoreSlim(1, 1));

            // CurrentCount is 1 when free, 0 when held — so "held by someone else" reads as 0 here.
            bool contended = gate.CurrentCount == 0;
            if (contended)
                _logger.Debug("Slskd search gate busy for {BaseUrl}; queuing search {SearchId} (waiting up to {AcquireCap}s)",
                    Settings.BaseUrl, searchId, (int)acquireCap.TotalSeconds);

            Stopwatch waitSw = Stopwatch.StartNew();
            if (!await gate.WaitAsync(acquireCap))
            {
                _logger.Warn("Slskd search gate timeout for {BaseUrl} after {ElapsedMs}ms; skipping search {SearchId} (Lidarr will retry the album)",
                    Settings.BaseUrl, waitSw.ElapsedMilliseconds, searchId);
                throw new SearchGateTimeoutException($"Could not acquire slskd search gate within {acquireCap.TotalSeconds:F0}s");
            }

            if (contended)
                _logger.Debug("Slskd search gate acquired for {SearchId} after {WaitMs}ms", searchId, waitSw.ElapsedMilliseconds);

            try
            {
                await ExecuteCreateSearchWithRetryAsync(searchRequest, searchId);
                await WaitOnSearchCompletionAsync(searchId, TimeSpan.FromSeconds(Settings.TimeoutInSeconds));
            }
            finally
            {
                gate.Release();
            }
        }

        // Even with the gate held, a third party may be hitting the same slskd (e.g. the user has the slskd
        // web UI open and started a manual search). Treat 429 as a transient and retry once after a short
        // backoff before giving up.
        private async Task ExecuteCreateSearchWithRetryAsync(HttpRequest searchRequest, string searchId)
        {
            try
            {
                await _client.ExecuteAsync(searchRequest);
                return;
            }
            catch (HttpException ex) when ((int)ex.Response.StatusCode == 429)
            {
                _logger.Debug(ex, "Slskd POST /searches returned 429 inside gate (external concurrency?); retrying in 2s for {SearchId}", searchId);
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
            await _client.ExecuteAsync(searchRequest);
        }

        private HttpRequest CreateResultRequest(string searchId, SearchQuery query)
        {
            HttpRequest request = new HttpRequestBuilder($"{Settings.BaseUrl}/api/v0/searches/{searchId}")
                .AddQueryParam("includeResponses", true)
                .SetHeader("X-API-KEY", Settings.ApiKey)
                .Build();

            TrackCountFilterType filterType = (TrackCountFilterType)Settings.TrackCountFilter;

            int minimumFiles = filterType switch
            {
                TrackCountFilterType.Exact or TrackCountFilterType.Lower or TrackCountFilterType.Unfitting
                    => Math.Max(Settings.MinimumResponseFileCount, query.TrackCount),
                _ => Settings.MinimumResponseFileCount
            };

            int? maximumFiles = filterType switch
            {
                TrackCountFilterType.Exact => query.TrackCount,
                TrackCountFilterType.Unfitting => query.TrackCount + Math.Max(2, (int)Math.Ceiling(Math.Log(query.TrackCount) * 1.67)),
                _ => null
            };

            request.ContentSummary = new
            {
                Album = query.Album ?? "",
                Artist = query.Artist,
                Interactive = query.Interactive,
                ExpandDirectory = query.ExpandDirectory,
                MimimumFiles = minimumFiles,
                MaximumFiles = maximumFiles
            }.ToJson();

            return request;
        }

        private async Task WaitOnSearchCompletionAsync(string searchId, TimeSpan timeout)
        {
            DateTime startTime = DateTime.UtcNow.AddSeconds(2);
            string state = "InProgress";
            int totalFilesFound = 0;
            bool hasTimedOut = false;
            DateTime timeoutEndTime = DateTime.UtcNow;

            while (state == "InProgress")
            {
                TimeSpan elapsed = DateTime.UtcNow - startTime;

                if (elapsed > timeout && !hasTimedOut)
                {
                    hasTimedOut = true;
                    timeoutEndTime = DateTime.UtcNow.AddSeconds(20);
                }
                else if (hasTimedOut && timeoutEndTime < DateTime.UtcNow)
                {
                    break;
                }

                JsonNode? searchStatus = await GetSearchResultsAsync(searchId);

                state = searchStatus?["state"]?.GetValue<string>() ?? "InProgress";
                int fileCount = searchStatus?["fileCount"]?.GetValue<int>() ?? 0;

                if (fileCount > totalFilesFound)
                    totalFilesFound = fileCount;

                double progress = Math.Clamp(fileCount / (double)Settings.FileLimit, 0.0, 1.0);
                double delay = hasTimedOut && DateTime.UtcNow < timeoutEndTime ? 1.0 : CalculateQuadraticDelay(progress);

                await Task.Delay(TimeSpan.FromSeconds(delay));
                if (state != "InProgress")
                    break;
            }
        }

        private async Task<JsonNode?> GetSearchResultsAsync(string searchId)
        {
            HttpRequest searchResultsRequest = new HttpRequestBuilder($"{Settings.BaseUrl}/api/v0/searches/{searchId}")
                     .SetHeader("X-API-KEY", Settings.ApiKey).Build();

            HttpResponse response = await _client.ExecuteAsync(searchResultsRequest);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                _logger.Warn("Failed to fetch search results for ID {SearchId}. Status: {StatusCode}", searchId, response.StatusCode);
                _logger.Debug("Response body: {Content}", response.Content);
                return null;
            }

            return JsonSerializer.Deserialize<JsonNode>(response.Content);
        }

        private static double CalculateQuadraticDelay(double progress)
        {
            const double a = 16;
            const double b = -16;
            const double c = 5;

            double delay = (a * Math.Pow(progress, 2)) + (b * progress) + c;
            return Math.Clamp(delay, 0.5, 5);
        }

        private static PrimaryAlbumType GetPrimaryAlbumType(string? albumType)
        {
            if (string.IsNullOrWhiteSpace(albumType))
                return PrimaryAlbumType.Album;

            PrimaryAlbumType? matchedType = PrimaryAlbumType.All
                .FirstOrDefault(t => t.Name.Equals(albumType, StringComparison.OrdinalIgnoreCase));

            return matchedType ?? PrimaryAlbumType.Album;
        }

        public async Task<IGrouping<string, SlskdFileData>?> ExpandDirectory(string username, string directoryPath, SlskdFileData originalTrack)
        {
            try
            {
                HttpRequest request = new HttpRequestBuilder($"{Settings.BaseUrl}/api/v0/users/{Uri.EscapeDataString(username)}/directory")
                    .SetHeader("X-API-KEY", Settings.ApiKey)
                    .SetHeader("Content-Type", "application/json")
                    .Post()
                    .Build();

                request.SetContent(JsonSerializer.Serialize(new { directory = directoryPath }));

                HttpResponse response = await _client.ExecuteAsync(request);

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    SlskdDirectoryApiResponse[]? directoryResponse = JsonSerializer.Deserialize<SlskdDirectoryApiResponse[]>(response.Content, _jsonOptions);

                    if (directoryResponse?.Length > 0 && directoryResponse[0].Files?.Any() == true)
                    {
                        string originalExtension = originalTrack.Extension?.ToLowerInvariant() ?? "";

                        List<SlskdFileData> directoryFiles = directoryResponse[0].Files
                            .Where(f => AudioFormatHelper.GetAudioCodecFromExtension(Path.GetExtension(f.Filename)) != AudioFormat.Unknown)
                            .Select(f =>
                            {
                                string fileExtension = Path.GetExtension(f.Filename)?.TrimStart('.').ToLowerInvariant() ?? "";
                                bool sameExtension = fileExtension == originalExtension;

                                return new SlskdFileData(
                                    Filename: $"{directoryPath}\\{f.Filename}",
                                    BitRate: sameExtension ? originalTrack.BitRate : null,
                                    BitDepth: sameExtension ? originalTrack.BitDepth : null,
                                    Size: f.Size,
                                    Length: sameExtension ? originalTrack.Length : null,
                                    Extension: fileExtension,
                                    SampleRate: sameExtension ? originalTrack.SampleRate : null,
                                    Code: f.Code,
                                    IsLocked: false
                                );
                            }).ToList();

                        if (directoryFiles.Count != 0)
                            return directoryFiles.GroupBy(f => SlskdTextProcessor.GetDirectoryFromFilename(f.Filename)).First();
                    }
                }
                else
                {
                    _logger.Debug("Directory API returned {Status} for {Username}:{Directory}", response.StatusCode, username, directoryPath);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error expanding directory {Username}:{Directory}", username, directoryPath);
            }

            return null;
        }
    }
}
