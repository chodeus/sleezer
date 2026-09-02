using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Indexers.Exceptions;
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
            pageableRequests.Add(GetRequests("never gonna give you up"));

            return pageableRequests;
        }

        public IndexerPageableRequestChain GetSearchRequests(AlbumSearchCriteria searchCriteria)
        {
            var chain = new IndexerPageableRequestChain();

            // Tier 1: the raw artist + entity title (AlbumQuery's "+Disambiguation" token never
            // matches). HttpIndexerBase only advances to the next tier when this returns nothing.
            var entityTitle = searchCriteria.Albums?.FirstOrDefault()?.Title ?? searchCriteria.AlbumTitle;
            var tier1 = $"{searchCriteria.ArtistQuery} {entityTitle}";
            chain.AddTier(GetRequests(tier1));

            if (string.IsNullOrWhiteSpace(entityTitle) || string.IsNullOrWhiteSpace(searchCriteria.ArtistQuery))
                return chain;

            // Tier 2: punctuation and edition/soundtrack qualifiers stripped so the core title
            // survives token-AND matching. Added only when it differs from tier 1.
            var artist = StoreQueryCleaner.CleanForTokenSearch(searchCriteria.CleanArtistQuery);
            var album = StoreQueryCleaner.CleanForTokenSearch(SearchCriteriaBase.GetQueryTitle(StoreQueryCleaner.StripQualifiers(entityTitle)));
            var tier2 = $"{artist} {album}";

            if (!string.Equals(tier2, tier1, StringComparison.OrdinalIgnoreCase))
                chain.AddTier(GetRequests(tier2));

            // Tier 3: MB split-release titles like "A / B" — Qobuz usually carries the halves
            // as separate releases, so search each; all halves share one tier.
            if (!entityTitle.Contains(" / ", StringComparison.Ordinal))
                return chain;

            List<string> partQueries =
            [
                .. entityTitle
                    .Split(" / ", StringSplitOptions.RemoveEmptyEntries)
                    .Select(part => StoreQueryCleaner.CleanForTokenSearch(SearchCriteriaBase.GetQueryTitle(StoreQueryCleaner.StripQualifiers(part))))
                    .Where(part => !string.IsNullOrWhiteSpace(part))
                    .Select(part => $"{artist} {part}")
                    .Where(query => !string.Equals(query, tier1, StringComparison.OrdinalIgnoreCase)
                                    && !string.Equals(query, tier2, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
            ];

            for (var i = 0; i < partQueries.Count; i++)
            {
                if (i == 0)
                    chain.AddTier(GetRequests(partQueries[i]));
                else
                    chain.Add(GetRequests(partQueries[i]));
            }

            return chain;
        }

        public IndexerPageableRequestChain GetSearchRequests(ArtistSearchCriteria searchCriteria)
        {
            var chain = new IndexerPageableRequestChain();
            chain.AddTier(GetRequests(searchCriteria.ArtistQuery));

            return chain;
        }

        private IEnumerable<IndexerRequest> GetRequests(string searchParameters)
        {
            QobuzAPI api = QobuzAPI.Instance
                ?? throw new ApiKeyException("Qobuz API is not initialised. Save the Qobuz indexer settings first.");

            // A scraped app secret can go stale between searches when Qobuz rotates it;
            // re-signing in re-reads bundle.js.
            if (!api.Client.IsAppSecretValid())
                api.SignIn(Settings);

            if (api.Login == null)
                throw new ApiKeyException("Qobuz login failed. Check your credentials in the indexer settings.");

            for (var page = 0; page < MaxPages; page++)
            {
                var data = new Dictionary<string, string>
                {
                    ["query"] = searchParameters,
                    ["limit"] = $"{PageSize}",
                    ["offset"] = $"{page * PageSize}",
                };

                var req = new IndexerRequest(api.GetAPIUrl("/album/search", data), HttpAccept.Json);
                req.HttpRequest.Method = System.Net.Http.HttpMethod.Get;
                req.HttpRequest.Headers.Add("X-App-ID", api.Client.AppId);
                req.HttpRequest.Headers.Add("X-User-Auth-Token", api.Login.AuthToken);
                yield return req;
            }
        }
    }
}
