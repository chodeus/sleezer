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
        public override bool IsBlocklisted(int artistId, ReleaseInfo release)
        {
            List<Blocklist> rows = MatchingRows(artistId, release);
            if (rows.Count == 0)
                return false;

            DateTime newest = rows.Max(r => r.Date);
            return DateTime.UtcNow - newest < SlskdDownloadItem.RetryBackoffWindow(rows.Count);
        }
    }

    public class QobuzBlocklist(IBlocklistRepository blocklistRepository) : BaseBlocklist<QobuzDownloadProtocol>(blocklistRepository)
    { }

    public class LucidaBlocklist(IBlocklistRepository blocklistRepository) : BaseBlocklist<LucidaDownloadProtocol>(blocklistRepository)
    { }

    public class SubSonicBlocklist(IBlocklistRepository blocklistRepository) : BaseBlocklist<SubSonicDownloadProtocol>(blocklistRepository)
    { }
}