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

            // Album entity title, NOT searchCriteria.AlbumQuery — AlbumQuery is
            // "{Title}+{Disambiguation}" and the fused "+..." token poisons the
            // Soulseek query (every term is mandatory). Self-titled albums keep
            // their title here too: the pipeline's SelfTitled handling dedupes
            // the query text, while the parser still needs the album name to
            // stamp matched directories.
            SearchContext context = new(
                Artist: searchCriteria.ArtistQuery,
                Album: album?.Title ?? searchCriteria.AlbumQuery,
                Year: searchCriteria.AlbumYear.ToString(),
                Interactive: searchCriteria.InteractiveSearch,
                TrackCount: trackCount,
                Aliases: searchCriteria.Artist?.Metadata.Value.Aliases ?? [],
                Tracks: tracks,
                Settings: Settings,
                ProcessedSearches: _processedSearches,
                SearchCriteria: searchCriteria)
            {
                TargetVariantTypes = album?.SecondaryTypes?.Select(t => t.Name).ToList() ?? []
            };

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

            // Raw artist name — CleanArtistQuery is "+"-joined ("Pink+Floyd"),
            // which no peer path contains.
            SearchContext context = new(
                Artist: searchCriteria.ArtistQuery,
                Album: null,
                Year: null,
                Interactive: searchCriteria.InteractiveSearch,
                TrackCount: trackCount,
                Aliases: searchCriteria.Artist?.Metadata.Value.Aliases ?? [],
                Tracks: tracks,
                Settings: Settings,
                ProcessedSearches: _processedSearches,
                SearchCriteria: searchCriteria)
            {
                TargetVariantTypes = album?.SecondaryTypes?.Select(t => t.Name).ToList() ?? []
            };

            return _searchPipeline.BuildChain(context, ExecuteSearch);
        }

        private IEnumerable<IndexerRequest> ExecuteSearch(SearchQuery query)
        {
            string? searchText = query.SearchText;
            if (string.IsNullOrWhiteSpace(searchText))
            {
                // Direct path (no pipeline query) still needs the server-blocked
                // term rewrite the pipeline normally applies.
                searchText = SlskdTextProcessor.RewriteRestrictedTerms(
                    SlskdTextProcessor.BuildSearchText(query.Artist, query.Album));
            }

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

                SlskdSearchRequestBody searchData = CreateSearchData(searchText);
                string searchId = searchData.Id;
                HttpRequest searchRequest = CreateSearchRequest(searchData);

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
            catch (RequestLimitReachedException)
            {
                // Gate timeout / persistent 429. Propagate so Lidarr records a
                // FAILURE with a short backoff instead of a clean "searched,
                // nothing found" — swallowing it here made a transient slskd
                // hiccup indistinguishable from "album does not exist".
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error generating search request for: {SearchText}", searchText);
                return null;
            }
        }

        private SlskdSearchRequestBody CreateSearchData(string searchText) => new()
        {
            Id = Guid.NewGuid().ToString(),
            FileLimit = Settings.FileLimit,
            FilterResponses = true,
            MaximumPeerQueueLength = Settings.MaximumPeerQueueLength,
            MinimumPeerUploadSpeed = Settings.MinimumPeerUploadSpeed,
            MinimumResponseFileCount = Settings.MinimumResponseFileCount,
            ResponseLimit = Settings.ResponseLimit,
            SearchText = searchText,
            SearchTimeout = (int)(Settings.TimeoutInSeconds * 1000),
        };

        private HttpRequest CreateSearchRequest(SlskdSearchRequestBody searchData)
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
            // Worst-case gate occupancy is Timeout + 2s head start + 20s grace
            // + ~7s cancel-wait; size the queue-wait cap against that so a
            // second queued search survives a slow first one.
            TimeSpan acquireCap = TimeSpan.FromSeconds(Math.Max(90, (Settings.TimeoutInSeconds + 30) * 2));
            SemaphoreSlim gate = _searchGates.GetOrAdd(Settings.BaseUrl ?? string.Empty, _ => new SemaphoreSlim(1, 1));

            // CurrentCount is 1 when free, 0 when held — so "held by someone else" reads as 0 here.
            bool contended = gate.CurrentCount == 0;
            if (contended)
                _logger.Debug("Slskd search gate busy for {BaseUrl}; queuing search {SearchId} (waiting up to {AcquireCap}s)",
                    Settings.BaseUrl, searchId, (int)acquireCap.TotalSeconds);

            Stopwatch waitSw = Stopwatch.StartNew();
            if (!await gate.WaitAsync(acquireCap))
            {
                _logger.Warn("Slskd search gate timeout for {BaseUrl} after {ElapsedMs}ms; failing search {SearchId} so Lidarr backs off and retries",
                    Settings.BaseUrl, waitSw.ElapsedMilliseconds, searchId);
                throw new RequestLimitReachedException(
                    $"Could not acquire slskd search gate within {acquireCap.TotalSeconds:F0}s — slskd is busy.",
                    TimeSpan.FromMinutes(2));
            }

            if (contended)
                _logger.Debug("Slskd search gate acquired for {SearchId} after {WaitMs}ms", searchId, waitSw.ElapsedMilliseconds);

            try
            {
                await ExecuteCreateSearchWithRetryAsync(searchRequest, searchId);
                string finalState;
                try
                {
                    finalState = await WaitOnSearchCompletionAsync(searchId, TimeSpan.FromSeconds(Settings.TimeoutInSeconds));
                }
                catch
                {
                    // Cleanup must not mask the original poll/wait failure.
                    try { await CancelInFlightSearchAsync(searchId); }
                    catch (Exception cleanupEx) { _logger.Debug(cleanupEx, "Cancel during abnormal wait abort failed for {SearchId}", searchId); }
                    throw;
                }

                // slskd's SearchTimeout is an INACTIVITY window, so a search with
                // a steady trickle of responses can outlive our client-side wait.
                // The parser deletes the search record as soon as it has read the
                // results, and deleting an in-flight search makes slskd's own
                // finalize write hit a vanished row ("Failed to finalize search",
                // DbUpdateConcurrencyException — verified live 2026-07-21, with
                // the search's results silently lost). Cancel first so the record
                // reaches a terminal state and the later delete is clean.
                // "Unknown" (a run of failed polls) says nothing about whether
                // the search still runs — if slskd was only transiently
                // unreachable, skipping the cancel here reopens the same
                // delete-in-flight race. A cancel against a truly vanished
                // search is a harmless 404.
                if (finalState.StartsWith("InProgress", StringComparison.OrdinalIgnoreCase) || finalState == "Unknown")
                    await CancelInFlightSearchAsync(searchId);
            }
            finally
            {
                gate.Release();
            }
        }

        private async Task CancelInFlightSearchAsync(string searchId)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    HttpRequest cancelRequest = new HttpRequestBuilder($"{Settings.BaseUrl}/api/v0/searches/{searchId}")
                        .SetHeader("X-API-KEY", Settings.ApiKey)
                        .Build();
                    cancelRequest.Method = HttpMethod.Put;
                    await _client.ExecuteAsync(cancelRequest);
                    break;
                }
                catch (HttpException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
                {
                    return;
                }
                catch (Exception ex) when (ex is HttpException or System.Net.WebException)
                {
                    // A transient cancel failure must not skip the terminal
                    // wait below — proceeding straight to the parser's DELETE
                    // is the exact finalize race this method exists to close.
                    _logger.Debug(ex, "Cancel attempt {Attempt} for in-flight search {SearchId} failed", attempt + 1, searchId);
                    if (attempt == 0)
                        await Task.Delay(TimeSpan.FromSeconds(1));
                }
            }

            for (int i = 0; i < 6; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                JsonNode? searchStatus = await GetSearchResultsAsync(searchId);
                string state = searchStatus?["state"]?.GetValue<string>() ?? "Unknown";
                if (!state.StartsWith("InProgress", StringComparison.OrdinalIgnoreCase))
                    return;
            }

            _logger.Debug("Search {SearchId} still in progress 6s after cancel; proceeding anyway", searchId);
        }

        // Even with the gate held, a third party may be hitting the same slskd (e.g. the user has the slskd
        // web UI open and started a manual search). Treat 429 as a transient and retry once after a short
        // backoff; if it persists, fail the search with a short indexer backoff instead of returning an
        // empty result Lidarr would record as "searched, nothing found".
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
            try
            {
                await _client.ExecuteAsync(searchRequest);
            }
            catch (HttpException ex) when ((int)ex.Response.StatusCode == 429)
            {
                throw new RequestLimitReachedException(
                    "slskd keeps returning 429 for new searches — another client is using it. Backing off.",
                    TimeSpan.FromMinutes(5));
            }
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

            request.ContentSummary = JsonSerializer.Serialize(new SlskdSearchData(
                Artist: query.Artist,
                Album: query.Album ?? "",
                Interactive: query.Interactive,
                ExpandDirectory: query.ExpandDirectory,
                MinimumFiles: minimumFiles,
                MaximumFiles: maximumFiles,
                TrackCount: query.TrackCount,
                Tracks: query.Tracks.Take(50).ToList(),
                TargetVariantTypes: query.TargetVariantTypes.Count > 0 ? query.TargetVariantTypes.ToList() : null));

            return request;
        }

        private async Task<string> WaitOnSearchCompletionAsync(string searchId, TimeSpan timeout)
        {
            DateTime startTime = DateTime.UtcNow.AddSeconds(2);
            string state = "InProgress";
            int totalFilesFound = 0;
            bool hasTimedOut = false;
            int consecutiveFailedPolls = 0;
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

                JsonNode? searchStatus;
                try
                {
                    searchStatus = await GetSearchResultsAsync(searchId);
                }
                catch (Exception ex) when (IsTransientPollFailure(ex))
                {
                    // Lidarr's HttpClient THROWS on 5xx/timeouts — treat those as
                    // failed polls so the counter engages instead of aborting the
                    // tier chain. Auth/permission/permanent errors (401/403/etc.)
                    // fall through and propagate: a bad API key must fail the
                    // search, never read as "searched, nothing found".
                    _logger.Debug(ex, "Search status poll failed transiently for {SearchId}", searchId);
                    searchStatus = null;
                }

                // A single failed poll is a transient worth riding out, but a
                // run of them means the search record is gone — spinning on
                // "InProgress" for the whole grace window just burns the gate.
                if (searchStatus == null)
                {
                    if (++consecutiveFailedPolls >= 3)
                        return "Unknown";
                }
                else
                {
                    consecutiveFailedPolls = 0;
                }

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

            return state;
        }

        // Transient = worth a retry-poll (5xx, 408, 429, network/timeout).
        // Everything else — notably 401/403/404 and other 4xx — is permanent
        // and must propagate so the search fails hard instead of decaying to
        // an empty result.
        private static bool IsTransientPollFailure(Exception ex)
        {
            if (ex is System.Net.WebException)
                return true;
            if (ex is HttpException httpEx)
            {
                int code = (int)httpEx.Response.StatusCode;
                return code >= 500 || code == 408 || code == 429;
            }
            return false;
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
