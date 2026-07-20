using NzbDrone.Plugin.Sleezer.Indexers.Soulseek.Search.Core;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek.Search.Transformers;

namespace NzbDrone.Plugin.Sleezer.Indexers.Soulseek.Search.Strategies;

public sealed class BaseSearchStrategy : SearchStrategyBase
{
    public override string Name => "Base Search";
    public override SearchTier Tier => SearchTier.Base;
    public override int Priority => 0;

    public override bool CanExecute(SearchContext context, QueryType queryType)
    {
        // Skip if special case already handled it
        if (queryType.HasFlag(QueryType.VariousArtists) ||
            queryType.HasFlag(QueryType.SelfTitled) ||
            queryType.HasFlag(QueryType.ShortName))
            return false;

        return !string.IsNullOrWhiteSpace(context.SearchArtist) ||
               !string.IsNullOrWhiteSpace(context.SearchAlbum);
    }

    // Plain "Artist Album" — every term in a Soulseek query is mandatory, so
    // year and release-type words belong in later variation tiers, not here
    // (live logs: "Artist Album Year Single" first-queries were the top
    // zero-result shape while the plain form often never ran).
    public override string? GetQuery(SearchContext context, QueryType queryType) =>
        QueryBuilder.Build(context.SearchArtist, context.SearchAlbum);
}

public sealed class YearSearchStrategy : SearchStrategyBase
{
    public override string Name => "Year Search";
    public override SearchTier Tier => SearchTier.Variation;
    public override int Priority => -10;

    public override bool IsEnabled(SlskdSettings settings) => settings.AppendYear;

    public override bool CanExecute(SearchContext context, QueryType queryType)
    {
        if (queryType.HasFlag(QueryType.VariousArtists) ||
            queryType.HasFlag(QueryType.SelfTitled) ||
            queryType.HasFlag(QueryType.ShortName))
            return false;

        return context.HasValidYear &&
               (!string.IsNullOrWhiteSpace(context.SearchArtist) ||
                !string.IsNullOrWhiteSpace(context.SearchAlbum));
    }

    public override string? GetQuery(SearchContext context, QueryType queryType) =>
        QueryBuilder.Build(context.SearchArtist, context.SearchAlbum, context.Year);
}
