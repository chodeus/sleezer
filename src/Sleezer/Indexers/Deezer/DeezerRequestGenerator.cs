using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using NzbDrone.Plugin.Sleezer.Deezer;

namespace NzbDrone.Core.Indexers.Deezer
{
    public class DeezerRequestGenerator : IIndexerRequestGenerator
    {
        private const int PageSize = 100;
        private const int MaxPages = 30;
        public DeezerIndexerSettings Settings { get; set; } = null!;
        public Logger Logger { get; set; } = null!;

        public virtual IndexerPageableRequestChain GetRecentRequests()
        {
            var pageableRequests = new IndexerPageableRequestChain();

            pageableRequests.AddTier(GetRequests($"never gonna give you up"));

            // TODO: this seems to cause problems in some cases, but I have yet to debug further, the above is a basic workaround which should work fine
            /*Dictionary<string, string> data = new()
            {
                { "gateway_input", new JObject()
                    {
                        ["PAGE"] = "channels/explore",
                        ["VERSION"] = "2.3",
                        ["SUPPORT"] = new JObject()
                        {
                            ["grid"] = new JArray()
                            {
                                "channel",
                                "album"
                            },
                            ["horizontal-grid"] = new JArray()
                            {
                                "album"
                            }
                        },
                        ["LANG"] = "us"
                    }.ToString(Newtonsoft.Json.Formatting.None)
                }
            };

            var url = DeezerAPI.Instance!.GetGWUrl("page.get", data);
            var req = new IndexerRequest(url, HttpAccept.Json);
            req.HttpRequest.Method = System.Net.Http.HttpMethod.Post;
            req.HttpRequest.Cookies.Add("sid", DeezerAPI.Instance.Client.SID);

            pageableRequests.Add(new[]
            {
                req
            });*/

            return pageableRequests;
        }

        public IndexerPageableRequestChain GetSearchRequests(AlbumSearchCriteria searchCriteria)
        {
            var chain = new IndexerPageableRequestChain();

            // Entity title, not AlbumQuery — its fused "+Disambiguation" token returns nothing inside a quoted field.
            var title = searchCriteria.Albums?.FirstOrDefault()?.Title ?? searchCriteria.AlbumTitle;
            var artist = searchCriteria.ArtistQuery;
            chain.AddTier(GetRequests($"artist:\"{artist}\" album:\"{title}\""));

            var stripped = StoreQueryCleaner.StripQualifiers(title);
            if (!string.Equals(stripped, title, StringComparison.OrdinalIgnoreCase))
                chain.AddTier(GetRequests($"artist:\"{artist}\" album:\"{stripped}\""));

            chain.AddTier(GetRequests(StoreQueryCleaner.CleanForTokenSearch(
                $"{searchCriteria.CleanArtistQuery} {SearchCriteriaBase.GetQueryTitle(stripped)}")));

            return chain;
        }

        public IndexerPageableRequestChain GetSearchRequests(ArtistSearchCriteria searchCriteria)
        {
            var chain = new IndexerPageableRequestChain();

            chain.AddTier(GetRequests($"artist:\"{searchCriteria.ArtistQuery}\""));
            chain.AddTier(GetRequests(searchCriteria.ArtistQuery));

            return chain;
        }

        private IEnumerable<IndexerRequest> GetRequests(string searchParameters)
        {
            DeezerAPI.Instance?.TryUpdateToken();

            for (var page = 0; page < MaxPages; page++)
            {
                JObject data = new()
                {
                    ["query"] = searchParameters,
                    ["start"] = $"{page * PageSize}",
                    ["nb"] = $"{PageSize}",
                    ["output"] = "ALBUM",
                    ["filter"] = "ALL",
                };

                var url = DeezerAPI.Instance!.GetGWUrl("search.music");
                var req = new IndexerRequest(url, HttpAccept.Json); ;
                req.HttpRequest.SetContent(data.ToString(Newtonsoft.Json.Formatting.None));
                req.HttpRequest.Method = System.Net.Http.HttpMethod.Post;
                req.HttpRequest.Cookies.Add("sid", DeezerAPI.Instance.Client.SID);
                yield return req;
            }
        }
    }
}
