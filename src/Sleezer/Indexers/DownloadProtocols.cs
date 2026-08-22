namespace NzbDrone.Core.Indexers
{
    public class DeezerDownloadProtocol : IDownloadProtocol { }
    public class SoulseekDownloadProtocol : IDownloadProtocol { }
    public class LucidaDownloadProtocol : IDownloadProtocol { }

    // Historical name: this one is DABMusic's (it proxies Qobuz). Renaming it would
    // orphan persisted DelayProfile rows and Blocklist entries, so the first-party
    // Qobuz client below gets its own protocol instead.
    public class QobuzDownloadProtocol : IDownloadProtocol { }
    public class QobuzDirectDownloadProtocol : IDownloadProtocol { }
    public class SubSonicDownloadProtocol : IDownloadProtocol { }
    public class AmazonMusicDownloadProtocol : IDownloadProtocol { }
    public class TidalDownloadProtocol : IDownloadProtocol { }
}
