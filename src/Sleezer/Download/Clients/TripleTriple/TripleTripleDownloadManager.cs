using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Download.Base;
using NzbDrone.Plugin.Sleezer.Indexers.TripleTriple;

using NzbDrone.Common.Disk;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Plugin.Sleezer.Core.PostProcessing;
using NzbDrone.Plugin.Sleezer.Metadata.FFmpeg;
namespace NzbDrone.Plugin.Sleezer.Download.Clients.TripleTriple
{
    public interface ITripleTripleDownloadManager : IBaseDownloadManager<TripleTripleDownloadRequest, TripleTripleDownloadOptions, TripleTripleClient> { }

    public class TripleTripleDownloadManager : BaseDownloadManager<TripleTripleDownloadRequest, TripleTripleDownloadOptions, TripleTripleClient>, ITripleTripleDownloadManager
    {
        private readonly IEnumerable<IHttpRequestInterceptor> _requestInterceptors;
        private readonly PostProcessRunner _postProcess;
        private readonly IAudioTagService _audioTagService;

        public TripleTripleDownloadManager(IEnumerable<IHttpRequestInterceptor> requestInterceptors, IAudioTagService audioTagService, Logger logger, ICorruptionScanner corruptionScanner, ICorruptionFailureHandler corruptionFailureHandler, IPreImportTagger preImportTagger, IMetadataFactory metadataFactory, IDiskProvider diskProvider) : base(logger)
        {
            _postProcess = new(corruptionScanner, corruptionFailureHandler, preImportTagger, metadataFactory, diskProvider, logger);
            _requestInterceptors = requestInterceptors;
            _audioTagService = audioTagService;
        }

        protected override Task<TripleTripleDownloadRequest> CreateDownloadRequest(
            RemoteAlbum remoteAlbum,
            IIndexer indexer,
            NamingConfig namingConfig,
            TripleTripleClient provider)
        {
            string baseUrl = provider.Settings.BaseUrl;
            bool isTrack = remoteAlbum.Release.DownloadUrl.StartsWith("track/");

            TripleTripleDownloadOptions options = new()
            {
                Handler = _requesthandler,
                DownloadPath = provider.Settings.DownloadPath,
                BaseUrl = baseUrl,
                MaxDownloadSpeed = provider.Settings.MaxDownloadSpeed * 1024,
                ConnectionRetries = provider.Settings.ConnectionRetries,
                NamingConfig = namingConfig,
                RequestInterceptors = _requestInterceptors,
                DelayBetweenAttemps = TimeSpan.FromSeconds(2),
                NumberOfAttempts = (byte)provider.Settings.ConnectionRetries,
                ClientInfo = DownloadClientItemClientInfo.FromDownloadClient(provider, false),
                PostProcess = _postProcess,
                PostProcessClient = PostProcessClient.TripleTriple,
                IsTrack = isTrack,
                ItemId = remoteAlbum.Release.DownloadUrl,
                CountryCode = ((TripleTripleCountry)provider.Settings.CountryCode).ToString(),
                Codec = (TripleTripleCodec)provider.Settings.Codec,
                DownloadLyrics = provider.Settings.DownloadLyrics,
                CreateLrcFile = provider.Settings.CreateLrcFile,
                EmbedLyrics = provider.Settings.EmbedLyrics,
                CoverSize = provider.Settings.CoverSize,
                AudioTagService = _audioTagService
            };

            _requesthandler.MaxParallelism = provider.Settings.MaxParallelDownloads;
            return Task.FromResult(new TripleTripleDownloadRequest(remoteAlbum, options));
        }
    }
}
