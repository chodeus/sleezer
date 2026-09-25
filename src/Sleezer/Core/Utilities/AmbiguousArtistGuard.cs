using NLog;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Core.Model;
using ReleaseTitleParser = NzbDrone.Core.Parser.Parser;

namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    /// <summary>Keeps a search from failing whole when a result's credited artist matches two library artists.</summary>
    // ArtistRepository.FindByName throws when two library artists share a CleanName, which fails the
    // whole search, the wanted album included, unless those results are retitled or dropped here.
    public static class AmbiguousArtistGuard
    {
        public static IList<ReleaseInfo> Apply(IList<ReleaseInfo> releases, Artist? searched, IEnumerable<Artist> library, string indexer, Logger logger) =>
            releases.Count == 0 ? releases : Apply(releases, searched, DuplicatedCleanNames(library), indexer, logger);

        public static IList<ReleaseInfo> Apply(IList<ReleaseInfo> releases, Artist? searched, HashSet<string> duplicates, string indexer, Logger logger)
        {
            if (releases.Count == 0 || duplicates.Count == 0 || string.IsNullOrWhiteSpace(searched?.CleanName))
                return releases;

            List<ReleaseInfo> kept = new(releases.Count);
            int retitled = 0;
            int dropped = 0;

            foreach (ReleaseInfo release in releases)
            {
                switch (Judge(release, searched, duplicates, out ReleaseTitleParts? parts))
                {
                    case Outcome.Keep:
                        kept.Add(release);
                        break;
                    case Outcome.Retitle:
                        ReleaseTitle.Render((StoreReleaseInfo)release, parts!);
                        release.Artist = searched.Name;
                        retitled++;
                        kept.Add(release);
                        break;
                    default:
                        dropped++;
                        break;
                }
            }

            if (retitled > 0 || dropped > 0)
                logger.Debug("{Indexer}: {Retitled} result(s) retitled and {Dropped} dropped for '{Artist}' — their credited artist matches more than one library artist",
                    indexer, retitled, dropped, searched.Name);

            return kept;
        }

        /// <summary>Whether Apply would drop this result; no side effects.</summary>
        public static bool Rejects(ReleaseInfo release, Artist searched, HashSet<string> duplicates) =>
            Judge(release, searched, duplicates, out _) == Outcome.Drop;

        // Mirrors FindByName's throw condition.
        public static HashSet<string> DuplicatedCleanNames(IEnumerable<Artist> library) =>
            [.. library.Where(a => a.CleanName != null).GroupBy(a => a.CleanName).Where(g => g.Count() > 1).Select(g => g.Key)];

        private enum Outcome { Keep, Retitle, Drop }

        private static Outcome Judge(ReleaseInfo release, Artist searched, HashSet<string> duplicates, out ReleaseTitleParts? parts)
        {
            parts = null;

            // The same parse Lidarr's DownloadDecisionMaker will do; an unparseable title never reaches FindByName.
            string? clean = ReleaseTitleParser.ParseAlbumTitle(release.Title)?.ArtistName?.CleanArtistName();
            if (clean == null || clean == searched.CleanName || !duplicates.Contains(clean))
                return Outcome.Keep;

            parts = RetitleAs(release, searched);
            return parts == null ? Outcome.Drop : Outcome.Retitle;
        }

        // Only where the store credits the searched artist as a main artist and the new title parses
        // back to them. Never a Various Artists credit: a compilation is not an ambiguity to resolve.
        private static ReleaseTitleParts? RetitleAs(ReleaseInfo release, Artist searched)
        {
            if (release is not StoreReleaseInfo { TitleParts: { } current } store
                || string.IsNullOrEmpty(release.Artist)
                || StoreReleaseVerifier.IsVariousArtists(release.Artist)
                || !store.MainArtists.Any(name => name.CleanArtistName() == searched.CleanName))
                return null;

            ReleaseTitleParts parts = current with { Artist = searched.Name };
            return ReleaseTitleParser.ParseAlbumTitle(parts.Text)?.ArtistName?.CleanArtistName() == searched.CleanName ? parts : null;
        }
    }
}
