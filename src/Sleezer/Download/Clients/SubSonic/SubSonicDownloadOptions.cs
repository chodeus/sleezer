using NzbDrone.Common.Http;
using NzbDrone.Core.Download;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Organizer;
using NzbDrone.Plugin.Sleezer.Core.PostProcessing;
using Requests.Options;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.SubSonic
{
    /// <summary>
    /// Download options specific to SubSonic downloads
    /// </summary>
    public record SubSonicDownloadOptions : RequestOptions<string, string>
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
        /// Base URL of the SubSonic server
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
        /// Naming configuration from Lidarr
        /// </summary>
        public NamingConfig? NamingConfig { get; set; }

        /// <summary>
        /// Whether this download is for a track (true) or album (false)
        /// </summary>
        public bool IsTrack { get; set; }

        /// <summary>
        /// The item ID to download
        /// </summary>
        public string ItemId { get; set; } = string.Empty;

        public IEnumerable<IHttpRequestInterceptor> RequestInterceptors { get; set; } = [];

        /// <summary>
        /// Lidarr's audio tag service, so tag writing follows Lidarr's rules and MBID mapping.
        /// </summary>
        public IAudioTagService AudioTagService { get; set; } = null!;

        /// <summary>
        /// Shared corruption-scan and pre-import-tagging pass. Null imports unverified.
        /// </summary>
        public PostProcessRunner? PostProcess { get; set; }

        /// <summary>
        /// SubSonic username for authentication
        /// </summary>
        public string Username { get; set; } = string.Empty;

        /// <summary>
        /// SubSonic password for authentication
        /// </summary>
        public string Password { get; set; } = string.Empty;

        /// <summary>
        /// Whether to use token-based authentication
        /// </summary>
        public bool UseTokenAuth { get; set; } = true;

        /// <summary>
        /// Preferred audio format for transcoding
        /// </summary>
        public PreferredFormatEnum PreferredFormat { get; set; } = PreferredFormatEnum.Raw;

        /// <summary>
        /// Maximum bit rate in kbps (0 for original quality)
        /// </summary>
        public int MaxBitRate { get; set; } = 0;

        public SubSonicDownloadOptions() { }

        protected SubSonicDownloadOptions(SubSonicDownloadOptions options) : base(options)
        {
            ClientInfo = options.ClientInfo;
            DownloadPath = options.DownloadPath;
            BaseUrl = options.BaseUrl;
            RequestTimeout = options.RequestTimeout;
            MaxDownloadSpeed = options.MaxDownloadSpeed;
            NamingConfig = options.NamingConfig;
            IsTrack = options.IsTrack;
            ItemId = options.ItemId;
            RequestInterceptors = options.RequestInterceptors;
            AudioTagService = options.AudioTagService;
            PostProcess = options.PostProcess;
            Username = options.Username;
            Password = options.Password;
            UseTokenAuth = options.UseTokenAuth;
            PreferredFormat = options.PreferredFormat;
            MaxBitRate = options.MaxBitRate;
        }
    }
}
