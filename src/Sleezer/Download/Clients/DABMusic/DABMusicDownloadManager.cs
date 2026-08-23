using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Download.Base;
using NzbDrone.Plugin.Sleezer.Indexers.DABMusic;

using NzbDrone.Common.Disk;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Plugin.Sleezer.Core.PostProcessing;
using NzbDrone.Plugin.Sleezer.Metadata.FFmpeg;
namespace NzbDrone.Plugin.Sleezer.Download.Clients.DABMusic
{
    public interface IDABMusicDownloadManager : IBaseDownloadManager<DABMusicDownloadRequest, DABMusicDownloadOptions, DABMusicClient>
    { }

    public class DABMusicDownloadManager : BaseDownloadManager<DABMusicDownloadRequest, DABMusicDownloadOptions, DABMusicClient>, IDABMusicDownloadManager
    {
        private readonly IDABMusicSessionManager _sessionManager;
        private readonly PostProcessRunner _postProcess;
        private readonly IEnumerable<IHttpRequestInterceptor> _requestInterceptors;
        private readonly IAudioTagService _audioTagService;

        public DABMusicDownloadManager(IDABMusicSessionManager sessionManager, IEnumerable<IHttpRequestInterceptor> requestInterceptors, IAudioTagService audioTagService, Logger logger, ICorruptionScanner corruptionScanner, ICorruptionFailureHandler corruptionFailureHandler, IPreImportTagger preImportTagger, IMetadataFactory metadataFactory, IDiskProvider diskProvider) : base(logger)
        {
            _postProcess = new(corruptionScanner, corruptionFailureHandler, preImportTagger, metadataFactory, diskProvider, logger);
            _sessionManager = sessionManager;
            _requestInterceptors = requestInterceptors;
            _audioTagService = audioTagService;
        }

        protected override async Task<DABMusicDownloadRequest> CreateDownloadRequest(
            RemoteAlbum remoteAlbum,
            IIndexer indexer,
            NamingConfig namingConfig,
            DABMusicClient provider)
        {
            string baseUrl = provider.Settings.BaseUrl;
            bool isTrack = remoteAlbum.Release.DownloadUrl.Contains("/track/");
            string itemId = remoteAlbum.Release.DownloadUrl.Split('/').Last();

            _logger.Trace($"Type from URL: {(isTrack ? "Track" : "Album")}, Extracted ID: {itemId}");

            DABMusicDownloadOptions options = new()
            {
                Handler = _requesthandler,
                DownloadPath = provider.Settings.DownloadPath,
                BaseUrl = baseUrl,
                MaxDownloadSpeed = provider.Settings.MaxDownloadSpeed * 1024, // Convert KB/s to bytes/s
                ConnectionRetries = provider.Settings.ConnectionRetries,
                NamingConfig = namingConfig,
                RequestInterceptors = _requestInterceptors,
                DelayBetweenAttemps = TimeSpan.FromSeconds(2),
                NumberOfAttempts = (byte)provider.Settings.ConnectionRetries,
                ClientInfo = DownloadClientItemClientInfo.FromDownloadClient(provider, false),
                PostProcess = _postProcess,
                PostProcessClient = PostProcessClient.DABMusic,
                IsTrack = isTrack,
                ItemId = itemId,
                Email = provider.Settings.Email,
                Password = provider.Settings.Password,
                AudioTagService = _audioTagService
            };

            _requesthandler.MaxParallelism = provider.Settings.MaxParallelDownloads;
            return new DABMusicDownloadRequest(remoteAlbum, _sessionManager, options);
        }
    }
}