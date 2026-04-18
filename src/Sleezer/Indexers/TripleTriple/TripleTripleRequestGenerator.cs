using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;

namespace NzbDrone.Plugin.Sleezer.Indexers.TripleTriple
{
    public interface ITripleTripleRequestGenerator : IIndexerRequestGenerator
    {
        void SetSetting(TripleTripleIndexerSettings settings);
    }

    public class TripleTripleRequestGenerator : ITripleTripleRequestGenerator
    {
        private readonly Logger _logger;
        private TripleTripleIndexerSettings? _settings;

        public TripleTripleRequestGenerator(Logger logger) => _logger = logger;

        public IndexerPageableRequestChain GetRecentRequests() => new();

        public IndexerPageableRequestChain GetSearchRequests(AlbumSearchCriteria searchCriteria)
        {
            string query = string.Join(' ', new[] { searchCriteria.AlbumQuery, searchCriteria.ArtistQuery }.Where(s => !string.IsNullOrWhiteSpace(s)));
            bool isSingle = searchCriteria.Albums?.FirstOrDefault()?.AlbumReleases?.Value?.Min(r => r.TrackCount) == 1;
            return Generate(query, isSingle);
        }

        public IndexerPageableRequestChain GetSearchRequests(ArtistSearchCriteria searchCriteria) => Generate(searchCriteria.ArtistQuery, false);

        public void SetSetting(TripleTripleIndexerSettings settings) => _settings = settings;

        private IndexerPageableRequestChain Generate(string query, bool isSingle)
        {
            IndexerPageableRequestChain chain = new();
            if (string.IsNullOrWhiteSpace(query))
            {
                _logger.Warn("Empty query, skipping search request");
                return chain;
            }

            string baseUrl = _settings!.BaseUrl.TrimEnd('/');
            string country = ((TripleTripleCountry)_settings.CountryCode).ToString();
            string codec = ((TripleTripleCodec)_settings.Codec).ToString().ToLowerInvariant();

            string url = $"{baseUrl}/api/amazon-music/search?query={Uri.EscapeDataString(query)}&types=track,album&country={country}";
            _logger.Trace("Creating TripleTriple search request: {Url}", url);

            HttpRequest req = new(url)
            {
                RequestTimeout = TimeSpan.FromSeconds(_settings.RequestTimeout),
                ContentSummary = new TripleTripleRequestData(baseUrl, country, codec, isSingle).ToJson()
            };
            req.Headers["User-Agent"] = NzbDrone.Plugin.Sleezer.UserAgent;
            req.Headers["Referer"] = $"{baseUrl}/search/{Uri.EscapeDataString(query)}";

            chain.AddTier([new IndexerRequest(req)]);

            if (isSingle)
            {
                string fallbackUrl = $"{baseUrl}/api/amazon-music/search?query={Uri.EscapeDataString(query)}&types=track&country={country}";
                _logger.Trace("Adding fallback track-only search: {Url}", fallbackUrl);

                HttpRequest fallbackReq = new(fallbackUrl)
                {
                    RequestTimeout = TimeSpan.FromSeconds(_settings.RequestTimeout),
                    ContentSummary = new TripleTripleRequestData(baseUrl, country, codec, true).ToJson()
                };
                fallbackReq.Headers["User-Agent"] = NzbDrone.Plugin.Sleezer.UserAgent;
                fallbackReq.Headers["Referer"] = $"{baseUrl}/search/{Uri.EscapeDataString(query)}";

                chain.AddTier([new IndexerRequest(fallbackReq)]);
            }

            return chain;
        }
    }
}
