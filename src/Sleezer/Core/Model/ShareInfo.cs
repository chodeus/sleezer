using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Plugin.Sleezer.Core.Model
{
    /// <summary>
    /// ReleaseInfo for a Soulseek share. Derives from TorrentInfo so the
    /// computed share priority can ride in <see cref="TorrentInfo.Seeders"/>,
    /// which SlskdIndexer.CleanupReleases folds into IndexerPriority — the
    /// ranking signal Lidarr's decision comparer actually consumes. Plain
    /// ReleaseInfo silently discarded the priority entirely.
    /// </summary>
    public class ShareInfo : TorrentInfo
    {
        /// <summary>
        /// True when the source directory fuzzy-matched the searched
        /// artist/album. Tiered search uses this to decide whether a tier
        /// actually found the wanted album or just returned bystander folders.
        /// </summary>
        public bool MatchedSearchCriteria { get; set; }
    }
}
