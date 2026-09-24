using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using NzbDrone.Plugin.Sleezer.Qobuz;

namespace NzbDrone.Core.Indexers.Qobuz
{
    public class QobuzRequestGenerator : IIndexerRequestGenerator
    {
        private const int PageSize = 100;

        // Qobuz.IsFullPage stops paging as soon as a page returns fewer than PageSize
        // distinct albums, so this is only a worst-case ceiling.
        private const int MaxPages = 5;

        public QobuzIndexerSettings Settings { get; set; } = null!;
        public Logger Logger { get; set; } = null!;

        public virtual IndexerPageableRequestChain GetRecentRequests()
        {
            // Qobuz has no new-release feed; this only exists so saving the indexer
            // settings has something to test against.
            var pageableRequests = new IndexerPageableRequestChain();
            pageableRequests.Add(GetRequests("never gonna give you up", null, false));

            return pageableRequests;
        }

        public IndexerPageableRequestChain GetSearchRequests(AlbumSearchCriteria searchCriteria)
        {
            var entityTitle = searchCriteria.Albums?.FirstOrDefault()?.Title ?? searchCriteria.AlbumTitle;
            var context = new QobuzSearchContext
            {
                ArtistCleanName = searchCriteria.Artist?.CleanName,
                MatchTitle = StoreQueryCleaner.MatchKey(entityTitle)
            };

            var chain = new IndexerPageableRequestChain();
            var tier = 0;

            // HttpIndexerBase runs every query of a tier in order and only moves on when the tier returned nothing.
            foreach (var query in QobuzQueryPlan.Build(searchCriteria.ArtistQuery, entityTitle))
            {
                if (query.Tier != tier)
                {
                    chain.AddTier(GetRequests(query.Query, context, query.Gated));
                    tier = query.Tier;
                }
                else
                    chain.Add(GetRequests(query.Query, context, query.Gated));
            }

            return chain;
        }

        public IndexerPageableRequestChain GetSearchRequests(ArtistSearchCriteria searchCriteria)
        {
            var chain = new IndexerPageableRequestChain();
            chain.AddTier(GetRequests(searchCriteria.ArtistQuery, new QobuzSearchContext { ArtistCleanName = searchCriteria.Artist?.CleanName }, false));

            return chain;
        }

        private IEnumerable<IndexerRequest> GetRequests(string searchParameters, QobuzSearchContext? context, bool gated)
        {
            // Enumerated only when HttpIndexerBase reaches it, after the earlier queries of the tier ran.
            if (gated && context is { MatchFound: true })
            {
                Logger.Debug("Qobuz: skipping fallback query '{Query}' — an earlier query already found the album", searchParameters);
                yield break;
            }

            // Enumerated lazily, so a session that went stale mid-search is re-signed here.
            QobuzAPI api = QobuzAPI.EnsureSignedIn(Settings, Logger);

            for (var page = 0; page < MaxPages; page++)
            {
                var data = new Dictionary<string, string>
                {
                    ["query"] = searchParameters,
                    ["limit"] = $"{PageSize}",
                    ["offset"] = $"{page * PageSize}",
                };

                var req = new QobuzIndexerRequest(api.GetAPIUrl("/album/search", data), context, api);
                req.HttpRequest.Method = System.Net.Http.HttpMethod.Get;
                req.HttpRequest.Headers.Add("X-App-ID", api.Client.AppId);
                req.HttpRequest.Headers.Add("X-User-Auth-Token", api.AuthToken);
                yield return req;
            }
        }
    }
}
