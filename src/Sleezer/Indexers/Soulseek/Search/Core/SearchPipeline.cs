using NLog;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Exceptions;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek.Search.Transformers;

namespace NzbDrone.Plugin.Sleezer.Indexers.Soulseek.Search.Core;

public interface ISlskdSearchChain
{
    LazyIndexerPageableRequestChain BuildChain(SearchContext context, SearchExecutor searchExecutor);
}

public sealed class SearchPipeline : ISlskdSearchChain
{
    private readonly IReadOnlyList<ISearchStrategy> _strategies;
    private readonly Logger _logger;

    public SearchPipeline(IEnumerable<ISearchStrategy> strategies, Logger logger)
    {
        _logger = logger;
        _strategies = strategies
            .OrderBy(s => s.Tier)
            .ThenBy(s => s.Priority)
            .ToList()
            .AsReadOnly();

        _logger.Debug("SearchPipeline: {Count} strategies loaded", _strategies.Count);
    }

    public LazyIndexerPageableRequestChain BuildChain(SearchContext context, SearchExecutor searchExecutor)
    {
        var chain = new LazyIndexerPageableRequestChain(context.Settings.MinimumResults);

        // Analyze and normalize once
        QueryType queryType = QueryAnalyzer.Analyze(context);
        SearchContext ctx = ApplyNormalization(context, queryType);

        _logger.Debug("Search: Artist='{Artist}', Album='{Album}', Type={QueryType}", ctx.Artist, ctx.Album, queryType);

        bool isFirst = true;
        foreach (var strategy in _strategies)
        {
            if (!strategy.IsEnabled(ctx.Settings) || !strategy.CanExecute(ctx, queryType))
                continue;

            Func<IEnumerable<IndexerRequest>> factory = () => ExecuteStrategy(strategy, ctx, queryType, searchExecutor);

            if (isFirst)
            {
                chain.AddFactory(factory);
                isFirst = false;
            }
            else
            {
                chain.AddTierFactory(factory);
            }
        }

        return chain;
    }

    private SearchContext ApplyNormalization(SearchContext context, QueryType queryType)
    {
        if (!queryType.HasFlag(QueryType.NeedsNormalization) || !context.Settings.NormalizedSeach)
            return context with { QueryType = queryType };

        var normalized = QueryNormalizer.Normalize(context with { QueryType = queryType });
        
        if (normalized.NormalizedArtist != null || normalized.NormalizedAlbum != null)
            _logger.Trace("Normalized: '{Artist}' / '{Album}'", normalized.NormalizedArtist ?? context.Artist, normalized.NormalizedAlbum ?? context.Album);

        return normalized;
    }

    private IEnumerable<IndexerRequest> ExecuteStrategy(
        ISearchStrategy strategy,
        SearchContext context,
        QueryType queryType,
        SearchExecutor searchExecutor)
    {
        string? query = strategy.GetQuery(context, queryType);

        if (string.IsNullOrWhiteSpace(query))
            return [];

        if (context.ProcessedSearches.Contains(query))
        {
            _logger.Trace("[{Strategy}] Skip duplicate: '{Query}'", strategy.Name, query);
            return [];
        }

        context.ProcessedSearches.Add(query);
        _logger.Debug("[{Strategy}] Search: '{Query}'", strategy.Name, query);

        try
        {
            var searchQuery = SearchQuery.FromContext(context) with { SearchText = query };
            return searchExecutor(searchQuery).ToList();
        }
        catch (RequestLimitReachedException)
        {
            // slskd is disconnected / temporarily banned — let Lidarr's indexer
            // backoff handler see it so the indexer gets disabled once, instead
            // of us swallowing it and logging a stack per strategy tier.
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "[{Strategy}] Error: '{Query}'", strategy.Name, query);
            return [];
        }
    }
}
