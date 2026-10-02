using System;
using System.Threading.Tasks;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download.Clients.Deezer;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser;
using NzbDrone.Plugin.Sleezer.Core.Deezer;
using NzbDrone.Plugin.Sleezer.Core.Replacements;
using NzbDrone.Plugin.Sleezer.Deezer;

namespace NzbDrone.Core.Indexers.Deezer
{
    public class Deezer : SleezerHttpIndexerBase<DeezerIndexerSettings>
    {
        public override string Name => "Deezer";
        public override string Protocol => nameof(DeezerDownloadProtocol);
        public override bool SupportsRss => false;
        public override bool SupportsSearch => true;
        public override int PageSize => 100;
        public override TimeSpan RateLimit => new TimeSpan(0);

        private readonly IDeezerProxy _deezerProxy;

        public Deezer(IDeezerProxy deezerProxy,
            IHttpClient httpClient,
            IIndexerStatusService indexerStatusService,
            IConfigService configService,
            IParsingService parsingService,
            IArtistService artistService,
            IMetadataFactory metadataFactory,
            Logger logger)
            : base(httpClient, indexerStatusService, configService, parsingService, artistService, metadataFactory, logger)
        {
            _deezerProxy = deezerProxy;
        }

        public override IIndexerRequestGenerator GetRequestGenerator()
        {
            // note: Firehawk no longer provides up-to-date Deezer tokens so this has no use anymore.
            /*if (string.IsNullOrEmpty(Settings.Arl))
            {
                var arlTask = ARLUtilities.GetFirstValidARL();
                arlTask.Wait();
                Settings.Arl = arlTask.Result;
            }*/

            return new DeezerRequestGenerator()
            {
                Api = DeezerAPI.ForArl(Settings.Arl),
                Settings = Settings,
                Logger = _logger
            };
        }

        protected override async Task<ValidationFailure> TestConnection()
        {
            ValidationFailure? baseFailure = await base.TestConnection();
            if (baseFailure != null)
                return baseFailure;

            string? streamingProblem = DeezerArlCheck.StreamingProblem(DeezerAPI.ForArl(Settings.Arl).Client.GWApi.ActiveUserData);
            return streamingProblem == null ? null! : new ValidationFailure(string.Empty, streamingProblem);
        }

        public override IParseIndexerResponse GetParser()
        {
            return new DeezerParser()
            {
                Settings = Settings,
                Rules = Rules
            };
        }
    }
}
