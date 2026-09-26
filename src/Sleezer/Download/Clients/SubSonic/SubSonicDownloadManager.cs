using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;

using NzbDrone.Common.Disk;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Plugin.Sleezer.Core.PostProcessing;
using Requests;
namespace NzbDrone.Plugin.Sleezer.Download.Clients.SubSonic
{
    public interface ISubSonicDownloadManager
    {
        Task<string> Download(RemoteAlbum remoteAlbum, IIndexer indexer, NamingConfig namingConfig, SubSonicClient provider);

        IEnumerable<DownloadClientItem> GetItems();

        void RemoveItem(DownloadClientItem item);
    }

    /// <summary>
    /// Manager for SubSonic downloads, handles creating and managing download requests
    /// </summary>
    public class SubSonicDownloadManager(IEnumerable<IHttpRequestInterceptor> requestInterceptors, IAudioTagService audioTagService, Logger logger, ICorruptionScanner corruptionScanner, ICorruptionFailureHandler corruptionFailureHandler, IPreImportTagger preImportTagger, IMetadataFactory metadataFactory, IDiskProvider diskProvider) : ISubSonicDownloadManager
    {
        private readonly RequestContainer<SubSonicDownloadRequest> _queue = [];
        private readonly Logger _logger = logger;
        private readonly RequestHandler _requesthandler = [];
        private readonly PostProcessRunner _postProcess = new(corruptionScanner, corruptionFailureHandler, preImportTagger, metadataFactory, diskProvider, logger);

        private readonly IEnumerable<IHttpRequestInterceptor> _requestInterceptors = requestInterceptors;
        private readonly IAudioTagService _audioTagService = audioTagService;

        public async Task<string> Download(RemoteAlbum remoteAlbum, IIndexer indexer, NamingConfig namingConfig, SubSonicClient provider)
        {
            try
            {
                SubSonicDownloadRequest downloadRequest = await CreateDownloadRequest(remoteAlbum, indexer, namingConfig, provider);
                _queue.Add(downloadRequest);

                _logger.Debug($"Added download: {downloadRequest.ID} | {remoteAlbum.Release.Title}");
                return downloadRequest.ID;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error adding download for album: {remoteAlbum.Release.Title}");
                throw;
            }
        }

        public IEnumerable<DownloadClientItem> GetItems() => _queue.Select(x => x.ClientItem);

        public void RemoveItem(DownloadClientItem item)
        {
            try
            {
                SubSonicDownloadRequest? request = _queue.ToList().Find(x => x.ID == item.DownloadId);
                if (request == null)
                {
                    _logger.Warn($"Attempted to remove non-existent download item: {item.DownloadId}");
                    return;
                }
                request.Dispose();
                _queue.Remove(request);
                _logger.Debug($"Removed download: {item.DownloadId}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Error removing download item: {item.DownloadId}");
            }
        }

        private async Task<SubSonicDownloadRequest> CreateDownloadRequest(
            RemoteAlbum remoteAlbum,
            IIndexer indexer,
            NamingConfig namingConfig,
            SubSonicClient provider)
        {
            string baseUrl = provider.Settings.ServerUrl.TrimEnd('/');
            string downloadUrl = remoteAlbum.Release.DownloadUrl;

            if (!downloadUrl.StartsWith(baseUrl))
                _logger.Warn("The expected URL does not match the configured API URL.");

            bool isTrack = downloadUrl.Contains("/track/");
            string itemId = ExtractIdFromUrl(downloadUrl);

            _logger.Trace($"Type from URL: {(isTrack ? "Track" : "Album")}, Extracted ID: {itemId}");

            SubSonicDownloadOptions options = new()
            {
                Handler = _requesthandler,
                DownloadPath = provider.Settings.DownloadPath,
                BaseUrl = baseUrl,
                Username = provider.Settings.Username,
                Password = provider.Settings.Password,
                UseTokenAuth = provider.Settings.UseTokenAuth,
                MaxDownloadSpeed = provider.Settings.MaxDownloadSpeed * 1024, // Convert KB/s to bytes/s
                RequestTimeout = provider.Settings.RequestTimeout,
                NamingConfig = namingConfig,
                RequestInterceptors = _requestInterceptors,
                DelayBetweenAttemps = TimeSpan.FromSeconds(2),
                NumberOfAttempts = (byte)provider.Settings.ConnectionRetries,
                ClientInfo = DownloadClientItemClientInfo.FromDownloadClient(provider, false),
                PostProcess = _postProcess,
                IsTrack = isTrack,
                ItemId = itemId,
                PreferredFormat = (PreferredFormatEnum)provider.Settings.PreferredFormat,
                MaxBitRate = provider.Settings.MaxBitRate,
                AudioTagService = _audioTagService
            };

            _requesthandler.MaxParallelism = provider.Settings.MaxParallelDownloads;

            return await Task.FromResult(new SubSonicDownloadRequest(remoteAlbum, options));
        }

        private static string ExtractIdFromUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
                return string.Empty;

            // The pattern is: {baseUrl}://album/{id} or {baseUrl}://track/{id}
            int albumPos = url.IndexOf("/album/", StringComparison.OrdinalIgnoreCase);
            if (albumPos >= 0)
                return url[(albumPos + "/album/".Length)..];

            int trackPos = url.IndexOf("/track/", StringComparison.OrdinalIgnoreCase);
            if (trackPos >= 0)
                return url[(trackPos + "/track/".Length)..];

            // Fallback: try to extract from path
            string[] parts = url.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0)
                return parts[^1];

            return string.Empty;
        }
    }
}