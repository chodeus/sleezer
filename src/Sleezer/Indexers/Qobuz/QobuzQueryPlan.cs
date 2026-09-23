using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Plugin.Sleezer.Core.Utilities;

namespace NzbDrone.Core.Indexers.Qobuz
{
    /// <summary>One query of an album search; a gated one is skipped once an earlier query in its tier found the album.</summary>
    public sealed record QobuzQuery(int Tier, string Query, bool Gated);

    /// <summary>The query plan for a Qobuz album search, in the order HttpIndexerBase runs it.</summary>
    public static class QobuzQueryPlan
    {
        public static List<QobuzQuery> Build(string artistQuery, string cleanArtistQuery, string entityTitle)
        {
            List<QobuzQuery> queries = [];

            void Add(int tier, string? query, bool gated)
            {
                if (string.IsNullOrWhiteSpace(query) || queries.Any(q => string.Equals(q.Query, query, StringComparison.OrdinalIgnoreCase)))
                    return;

                queries.Add(new QobuzQuery(tier, query, gated));
            }

            // Tier 1: the raw artist + entity title (AlbumQuery's "+Disambiguation" token never matches).
            Add(1, $"{artistQuery} {entityTitle}".Trim(), false);

            if (string.IsNullOrWhiteSpace(entityTitle) || string.IsNullOrWhiteSpace(artistQuery))
                return queries;

            var artist = StoreQueryCleaner.CleanForTokenSearch(StoreQueryCleaner.CollapseAcronyms(cleanArtistQuery));
            if (string.IsNullOrWhiteSpace(artist))
                return queries;

            // Same tier, gated: a tier-1 hit is often the wrong edition or another artist, so this
            // query runs unless an earlier query already found the album.
            Add(1, Compose(artist, Clean(entityTitle)), true);

            // Also tier 1 and gated: HttpIndexerBase stops at the first tier with any result, and the
            // raw query nearly always returns something, so a later tier would never run.
            Add(1, Compose(artist, Clean(StoreQueryCleaner.StripTrailingSubtitle(entityTitle))), true);

            // MB split-release titles like "A / B" — Qobuz usually carries the halves separately.
            if (entityTitle.Contains(" / ", StringComparison.Ordinal))
            {
                foreach (var part in entityTitle.Split(" / ", StringSplitOptions.RemoveEmptyEntries))
                    Add(1, Compose(artist, Clean(part)), true);
            }

            return queries;
        }

        private static string Clean(string title) =>
            StoreQueryCleaner.CleanForTokenSearch(SearchCriteriaBase.GetQueryTitle(StoreQueryCleaner.StripForSearch(StoreQueryCleaner.CollapseAcronyms(title))));

        private static string? Compose(string artist, string album) =>
            string.IsNullOrWhiteSpace(album) ? null : $"{artist} {album}";
    }
}
