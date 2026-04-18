using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;
using System.Text.Json;
using NzbDrone.Plugin.Sleezer.Download.Base;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.TripleTriple
{
    public class TripleTripleClient : DownloadClientBase<TripleTripleProviderSettings>
    {
        private readonly ITripleTripleDownloadManager _downloadManager;
        private readonly INamingConfigService _namingService;
        private readonly IEnumerable<IHttpRequestInterceptor> _requestInterceptors;

        public TripleTripleClient(
            ITripleTripleDownloadManager downloadManager,
            IConfigService configService,
            IDiskProvider diskProvider,
            INamingConfigService namingConfigService,
            IRemotePathMappingService remotePathMappingService,
            ILocalizationService localizationService,
            IEnumerable<IHttpRequestInterceptor> requestInterceptors,
            Logger logger)
            : base(configService, diskProvider, remotePathMappingService, localizationService, logger)
        {
            _downloadManager = downloadManager;
            _requestInterceptors = requestInterceptors;
            _namingService = namingConfigService;
        }

        public override string Name => "T2Tunes";
        public override string Protocol => nameof(AmazonMusicDownloadProtocol);
        public new TripleTripleProviderSettings Settings => base.Settings;

        public override Task<string> Download(RemoteAlbum remoteAlbum, IIndexer indexer) => _downloadManager.Download(remoteAlbum, indexer, _namingService.GetConfig(), this);

        public override IEnumerable<DownloadClientItem> GetItems() => _downloadManager.GetItems();

        public override void RemoveItem(DownloadClientItem item, bool deleteData)
        {
            if (deleteData)
                DeleteItemData(item);
            _downloadManager.RemoveItem(item);
        }

        public override DownloadClientInfo GetStatus() => new()
        {
            IsLocalhost = false,
            OutputRootFolders = [new OsPath(Settings.DownloadPath)]
        };

        protected override void Test(List<ValidationFailure> failures)
        {
            if (!_diskProvider.FolderExists(Settings.DownloadPath))
            {
                failures.Add(new ValidationFailure("DownloadPath", "Download path does not exist"));
                return;
            }

            if (!_diskProvider.FolderWritable(Settings.DownloadPath))
            {
                failures.Add(new ValidationFailure("DownloadPath", "Download path is not writable"));
                return;
            }

            try
            {
                BaseHttpClient httpClient = new(Settings.BaseUrl.Trim(), _requestInterceptors, TimeSpan.FromSeconds(30));
                string response = httpClient.GetStringAsync("/api/status").GetAwaiter().GetResult();

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

                _logger.Debug("Successfully connected to T2Tunes, status: up");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error connecting to T2Tunes instance");
                failures.Add(new ValidationFailure("BaseUrl", $"Cannot connect to T2Tunes instance: {ex.Message}"));
            }
        }
    }
}
