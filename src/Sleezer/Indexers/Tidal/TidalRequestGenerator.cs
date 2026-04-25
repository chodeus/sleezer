using System.Collections.Generic;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Plugin.Sleezer.Tidal;

namespace NzbDrone.Core.Indexers.Tidal
{
    public class TidalRequestGenerator : IIndexerRequestGenerator
    {
        private const int PageSize = 100;
        private const int MaxPages = 3;

        public TidalIndexerSettings Settings { get; set; } = null!;
        public Logger Logger { get; set; } = null!;

        public virtual IndexerPageableRequestChain GetRecentRequests()
        {
            // Lidarr requires a non-throwing search for the indexer Test button.
            // Tidal has no public RSS / new-release feed; this is a fixed dummy
            // query just so something runs.
            var pageableRequests = new IndexerPageableRequestChain();
            pageableRequests.Add(GetRequests("never gonna give you up"));
            return pageableRequests;
        }

        public IndexerPageableRequestChain GetSearchRequests(AlbumSearchCriteria searchCriteria)
        {
            var chain = new IndexerPageableRequestChain();
            chain.AddTier(GetRequests($"{searchCriteria.ArtistQuery} {searchCriteria.AlbumQuery}"));
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
            var instance = TidalAPI.Instance
                ?? throw new System.InvalidOperationException(
                    "Tidal API not initialized. Authenticate the indexer in plugin settings first.");

            for (var page = 0; page < MaxPages; page++)
            {
                var data = new Dictionary<string, string>
                {
                    ["query"] = searchParameters,
                    ["limit"] = $"{PageSize}",
                    ["types"] = "albums,tracks",
                    ["offset"] = $"{page * PageSize}"
                };

                var url = instance.GetAPIUrl("search", data);
                var req = new IndexerRequest(url, HttpAccept.Json);
                req.HttpRequest.Method = System.Net.Http.HttpMethod.Get;

                var user = instance.Client.ActiveUser;
                if (user != null)
                    req.HttpRequest.Headers.Add("Authorization", $"{user.TokenType} {user.AccessToken}");

                yield return req;
            }
        }
    }
}
