using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser;
using NzbDrone.Core.ThingiProvider;
using System.Text.Json;
using NzbDrone.Plugin.Sleezer.Download.Base;

namespace NzbDrone.Plugin.Sleezer.Indexers.TripleTriple
{
    public class TripleTripleIndexer : HttpIndexerBase<TripleTripleIndexerSettings>
    {
        private readonly ITripleTripleRequestGenerator _requestGenerator;
        private readonly ITripleTripleParser _parser;
        private readonly IEnumerable<IHttpRequestInterceptor> _requestInterceptors;

        public override string Name => "T2Tunes";
        public override string Protocol => nameof(AmazonMusicDownloadProtocol);
        public override bool SupportsRss => false;
        public override bool SupportsSearch => true;
        public override int PageSize => 50;
        public override TimeSpan RateLimit => TimeSpan.FromSeconds(1);

        public override ProviderMessage Message => new("T2Tunes (TripleTriple) provides high-quality music downloads from Amazon Music.", ProviderMessageType.Info);

        public TripleTripleIndexer(
            ITripleTripleRequestGenerator requestGenerator,
            ITripleTripleParser parser,
            IHttpClient httpClient,
            IIndexerStatusService statusService,
            IConfigService configService,
            IParsingService parsingService,
            IEnumerable<IHttpRequestInterceptor> requestInterceptors,
            Logger logger)
            : base(httpClient, statusService, configService, parsingService, logger)
        {
            _requestGenerator = requestGenerator;
            _parser = parser;
            _requestInterceptors = requestInterceptors;
        }

        protected override async Task Test(List<ValidationFailure> failures)
        {
            try
            {
                BaseHttpClient httpClient = new(Settings.BaseUrl.Trim(), _requestInterceptors, TimeSpan.FromSeconds(30));
                string response = await httpClient.GetStringAsync("/api/status");

                if (string.IsNullOrEmpty(response))
                {
                    failures.Add(new ValidationFailure("BaseUrl", "Cannot connect to T2Tunes instance: Empty response"));
                    return;
                }

                JsonDocument doc = JsonDocument.Parse(response);
                if (!doc.RootElement.TryGetProperty("amazonMusic", out JsonElement statusElement) ||
                    statusElement.GetString()?.ToLower() != "up")
                {
                    failures.Add(new ValidationFailure("BaseUrl", "T2Tunes Amazon Music service is not available"));
                    return;
                }

                _logger.Debug("Successfully connected to T2Tunes, Amazon Music status: up");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error connecting to T2Tunes API");
                failures.Add(new ValidationFailure("BaseUrl", $"Cannot connect to T2Tunes instance: {ex.Message}"));
            }
        }

        public override IIndexerRequestGenerator GetRequestGenerator()
        {
            _requestGenerator.SetSetting(Settings);
            return _requestGenerator;
        }

        public override IParseIndexerResponse GetParser() => _parser;
    }
}
