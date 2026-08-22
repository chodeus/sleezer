using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;

namespace NzbDrone.Plugin.Sleezer.Blocklisting
{
    public class SoulseekBlocklist(IBlocklistRepository blocklistRepository) : BaseBlocklist<SoulseekDownloadProtocol>(blocklistRepository)
    {
        // Soulseek failures are often transient (peer offline, queue full), so a
        // permanent native block is too harsh. Each failure appends a row, so
        // the row count is the attempt count: honour the same escalating window
        // (1h → 6h → 24h) the parser-side skip uses, decaying naturally so a
        // recovered peer becomes grabbable again.
        // Only failures within this window count toward the escalation tier,
        // so a peer that fails intermittently over months isn't pinned to the
        // 24h tier forever — the cadence decays back toward 1h after a healthy
        // gap.
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

    public class DABMusicBlocklist(IBlocklistRepository blocklistRepository) : BaseBlocklist<DABMusicDownloadProtocol>(blocklistRepository)
    { }

    public class QobuzBlocklist(IBlocklistRepository blocklistRepository) : BaseBlocklist<QobuzDownloadProtocol>(blocklistRepository)
    { }

    public class LucidaBlocklist(IBlocklistRepository blocklistRepository) : BaseBlocklist<LucidaDownloadProtocol>(blocklistRepository)
    { }

    public class SubSonicBlocklist(IBlocklistRepository blocklistRepository) : BaseBlocklist<SubSonicDownloadProtocol>(blocklistRepository)
    { }

    public class BandcampBlocklist(IBlocklistRepository blocklistRepository) : BaseBlocklist<BandcampDownloadProtocol>(blocklistRepository)
    { }
}