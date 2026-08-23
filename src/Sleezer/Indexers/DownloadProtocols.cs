namespace NzbDrone.Core.Indexers
{
    public class DeezerDownloadProtocol : IDownloadProtocol { }
    public class SoulseekDownloadProtocol : IDownloadProtocol { }
    public class LucidaDownloadProtocol : IDownloadProtocol { }

    // DABMusic proxies Qobuz but is not Qobuz; it used to hold the Qobuz name, which
    // left the first-party client below with nowhere sensible to go.
    public class DABMusicDownloadProtocol : IDownloadProtocol { }
    public class QobuzDownloadProtocol : IDownloadProtocol { }
    public class SubSonicDownloadProtocol : IDownloadProtocol { }
    public class AmazonMusicDownloadProtocol : IDownloadProtocol { }
    public class TidalDownloadProtocol : IDownloadProtocol { }
    public class BandcampDownloadProtocol : IDownloadProtocol { }
}
