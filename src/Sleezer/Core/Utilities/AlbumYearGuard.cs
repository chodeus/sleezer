using NLog;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Core.Model;

namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    /// <summary>Drops results for a different album that shares the searched title.</summary>
    public static class AlbumYearGuard
    {
        // Store and MusicBrainz dates disagree by a year often enough that an exact-only
        // rule would drop correct results; two years apart is a different record.
        private const int ToleranceYears = 1;

        // Parsers stamp UtcNow when they have no usable date, so a stamp this fresh is that
        // sentinel, not a real release date.
        private static readonly TimeSpan JustStamped = TimeSpan.FromHours(1);

        public static IList<ReleaseInfo> Apply(IList<ReleaseInfo> releases, AlbumSearchCriteria? criteria, string indexerName, Logger logger)
        {
            int targetYear = criteria?.AlbumYear ?? 0;

            if (releases.Count == 0 || targetYear <= 0)
                return releases;

            DateTime nowUtc = DateTime.UtcNow;

            // Only engage when something demonstrably matches. A catalogue whose store years
            // are uniformly a year or two off MusicBrainz must be left alone, not emptied.
            if (!releases.Any(r => !IsUndated(r, nowUtc) && r.PublishDate.Year == targetYear))
                return releases;

            List<ReleaseInfo> flagged = [];
            foreach (ReleaseInfo release in releases)
            {
                // Unjudgeable, not wrong — and on the AlbumData indexers that is most of them.
                if (IsUndated(release, nowUtc) || Math.Abs(release.PublishDate.Year - targetYear) <= ToleranceYears)
                    continue;

                flagged.Add(release);
                if (release is StoreReleaseInfo store)
                    store.Rejection ??= $"released {release.PublishDate.Year}; the searched album is from {targetYear}";
            }

            if (flagged.Count == 0)
                return releases;

            // Summary at Info, detail at Debug: a flagged release is the answer to "why did
            // it not grab that", and Lidarr logs at Info.
            logger.Info("{Indexer}: flagged {Flagged} of {Total} result(s) not released around {TargetYear}",
                indexerName, flagged.Count, releases.Count, targetYear);

            foreach (ReleaseInfo release in flagged)
            {
                logger.Debug("{Indexer} flagged '{Title}' ({Year}) — the searched album is from {TargetYear}",
                    indexerName, release.Title, release.PublishDate.Year, targetYear);
            }

            return releases;
        }

        private static bool IsUndated(ReleaseInfo release, DateTime nowUtc)
        {
            if (release.PublishDate == default)
                return true;

            // A future date is a scheduled release, never the sentinel.
            TimeSpan age = nowUtc - release.PublishDate.ToUniversalTime();
            return age >= TimeSpan.Zero && age < JustStamped;
        }
    }
}
