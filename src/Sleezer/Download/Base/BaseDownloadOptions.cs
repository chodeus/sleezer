using NzbDrone.Common.Http;
using NzbDrone.Core.Download;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Organizer;
using NzbDrone.Plugin.Sleezer.Core.PostProcessing;
using NzbDrone.Plugin.Sleezer.Metadata.FFmpeg;
using Requests.Options;

namespace NzbDrone.Plugin.Sleezer.Download.Base
{
    /// <summary>
    /// Base options for download requests containing common configuration
    /// </summary>
    public record BaseDownloadOptions : RequestOptions<string, string>
    {
        /// <summary>
        /// Client info for tracking the download in Lidarr
        /// </summary>
        public DownloadClientItemClientInfo? ClientInfo { get; set; }

        /// <summary>
        /// Path where downloads will be stored
        /// </summary>
        public string DownloadPath { get; set; } = string.Empty;

        /// <summary>
        /// Base URL of the service instance
        /// </summary>
        public string BaseUrl { get; set; } = string.Empty;

        /// <summary>
        /// Timeout for HTTP requests in seconds
        /// </summary>
        public int RequestTimeout { get; set; } = 60;

        /// <summary>
        /// Maximum download speed in bytes per second (0 = unlimited)
        /// </summary>
        public int MaxDownloadSpeed { get; set; }

        /// <summary>
        /// Number of chunks for download
        /// </summary>
        public int Chunks { get; set; } = 1;

        /// <summary>
        /// Number of times to retry connections
        /// </summary>
        public int ConnectionRetries { get; set; } = 3;

        /// <summary>
        /// Naming configuration from Lidarr
        /// </summary>
        public NamingConfig? NamingConfig { get; set; }

        /// <summary>
        /// Whether this download is for a track (true) or album (false)
        /// </summary>
        public bool IsTrack { get; set; }

        /// <summary>
        /// The item ID/URL to download from
        /// </summary>
        public string ItemId { get; set; } = string.Empty;

        /// <summary>
        /// Request tnterceptors for requests
        /// </summary>
        public IEnumerable<IHttpRequestInterceptor> RequestInterceptors { get; set; } = [];

        /// <summary>
        /// Lidarr audio tag service for pre-import tagging. Each download manager
        /// injects this via DI and populates it when building options. Requests
        /// delegate tag writing to Lidarr so the plugin inherits Lidarr's tag
        /// rules, format handling, and MBID mapping.
        /// </summary>
        public IAudioTagService AudioTagService { get; set; } = null!;

        /// <summary>
        /// Shared corruption-scan and pre-import-tagging pass. Null leaves the client
        /// importing unverified, which is what every web client did before this existed.
        /// </summary>
        public PostProcessRunner? PostProcess { get; set; }

        /// <summary>
        /// Which client this download belongs to, so the runner can honour the per-client
        /// opt-in lists in the FFmpeg metadata settings.
        /// </summary>
        public PostProcessClient PostProcessClient { get; set; }

        public BaseDownloadOptions() { }

        protected BaseDownloadOptions(BaseDownloadOptions options) : base(options)
        {
            ClientInfo = options.ClientInfo;
            DownloadPath = options.DownloadPath;
            BaseUrl = options.BaseUrl;
            RequestTimeout = options.RequestTimeout;
            MaxDownloadSpeed = options.MaxDownloadSpeed;
            ConnectionRetries = options.ConnectionRetries;
            NamingConfig = options.NamingConfig;
            IsTrack = options.IsTrack;
            Chunks = options.Chunks;
            ItemId = options.ItemId;
            RequestInterceptors = options.RequestInterceptors;
            AudioTagService = options.AudioTagService;
            PostProcess = options.PostProcess;
            PostProcessClient = options.PostProcessClient;
        }
    }
}