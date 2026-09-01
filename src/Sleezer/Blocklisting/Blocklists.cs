using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;

namespace NzbDrone.Plugin.Sleezer.Blocklisting
{
    public class SoulseekBlocklist(IBlocklistRepository blocklistRepository) : BaseBlocklist<SoulseekDownloadProtocol>(blocklistRepository)
    {
        // Only failures inside this window count toward the 1h → 6h → 24h tier, so
        // an intermittently failing peer decays back to 1h instead of pinning at 24h.
        private static readonly TimeSpan EscalationWindow = TimeSpan.FromDays(30);

        public override bool IsBlocklisted(int artistId, ReleaseInfo release)
        {
            DateTime cutoff = DateTime.UtcNow - EscalationWindow;
            List<Blocklist> rows = [.. MatchingRows(artistId, release).Where(r => r.Date >= cutoff)];
            if (rows.Count == 0)
                return false;

            DateTime newest = rows.Max(r => r.Date);
            return DateTime.UtcNow - newest < SlskdDownloadItem.RetryBackoffWindow(rows.Count);
        }
    }

    public class QobuzBlocklist(IBlocklistRepository blocklistRepository) : BaseBlocklist<QobuzDownloadProtocol>(blocklistRepository)
    { }

    public class SubSonicBlocklist(IBlocklistRepository blocklistRepository) : BaseBlocklist<SubSonicDownloadProtocol>(blocklistRepository)
    { }

    public class BandcampBlocklist(IBlocklistRepository blocklistRepository) : BaseBlocklist<BandcampDownloadProtocol>(blocklistRepository)
    { }
}