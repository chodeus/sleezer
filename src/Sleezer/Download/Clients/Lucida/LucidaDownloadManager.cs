using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Download.Base;
using NzbDrone.Plugin.Sleezer.Indexers.Lucida;

using NzbDrone.Common.Disk;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Plugin.Sleezer.Core.PostProcessing;
using NzbDrone.Plugin.Sleezer.Metadata.FFmpeg;
namespace NzbDrone.Plugin.Sleezer.Download.Clients.Lucida
{
    /// <summary>
    /// Interface for the Lucida download manager
    /// </summary>
    public interface ILucidaDownloadManager : IBaseDownloadManager<LucidaDownloadRequest, LucidaDownloadOptions, LucidaClient>;

    /// <summary>
    /// Lucida download manager using the base download manager implementation
    /// </summary>
    public class LucidaDownloadManager(
        Logger logger,
        IEnumerable<IHttpRequestInterceptor> requestInterceptors,
        ILucidaRateLimiter rateLimiter,
        ICorruptionScanner corruptionScanner, ICorruptionFailureHandler corruptionFailureHandler, IPreImportTagger preImportTagger, IMetadataFactory metadataFactory, IDiskProvider diskProvider
    ) : BaseDownloadManager<LucidaDownloadRequest, LucidaDownloadOptions, LucidaClient>(logger), ILucidaDownloadManager
    {
        private readonly PostProcessRunner _postProcess = new(corruptionScanner, corruptionFailureHandler, preImportTagger, metadataFactory, diskProvider, logger);

        protected override async Task<LucidaDownloadRequest> CreateDownloadRequest(
            RemoteAlbum remoteAlbum,
            IIndexer indexer,
            NamingConfig namingConfig,
            LucidaClient provider)
        {
            string itemUrl = remoteAlbum.Release.DownloadUrl;
            string baseUrl = ((LucidaIndexerSettings)indexer.Definition.Settings).BaseUrl;

            _logger.Trace($"Processing Lucida download URL: {itemUrl} on Instance: {baseUrl}");

            bool isTrack = remoteAlbum.Release.Source == "track";
            _logger.Trace($"Type from Source field: {remoteAlbum.Release.Source} -> {(isTrack ? "Track" : "Album")}");

            LucidaDownloadOptions options = new()
            {
                Handler = _requesthandler,
                DownloadPath = provider.Settings.DownloadPath,
                BaseUrl = baseUrl,
                RequestTimeout = provider.Settings.RequestTimeout,
                MaxDownloadSpeed = provider.Settings.MaxDownloadSpeed * 1024, // Convert KB/s to bytes/s
                ConnectionRetries = provider.Settings.ConnectionRetries,
                NamingConfig = namingConfig,
                DelayBetweenAttemps = TimeSpan.FromSeconds(2),
                RequestInterceptors = requestInterceptors,
                NumberOfAttempts = (byte)provider.Settings.ConnectionRetries,
                ClientInfo = DownloadClientItemClientInfo.FromDownloadClient(provider, false),
                PostProcess = _postProcess,
                PostProcessClient = PostProcessClient.Lucida,
                IsTrack = isTrack,
                ItemId = itemUrl,
                RateLimiter = rateLimiter
            };

            _requesthandler.MaxParallelism = provider.Settings.MaxParallelDownloads;
            await Task.Yield();
            return new LucidaDownloadRequest(remoteAlbum, options);
        }
    }
}