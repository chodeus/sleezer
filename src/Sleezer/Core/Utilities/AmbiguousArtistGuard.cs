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
        public static IList<ReleaseInfo> Apply(IList<ReleaseInfo> releases, Artist? searched, IEnumerable<Artist> library, string indexer, Logger logger)
        {
            if (releases.Count == 0 || string.IsNullOrWhiteSpace(searched?.CleanName))
                return releases;

            // Mirrors FindByName's throw condition.
            HashSet<string> duplicates = [.. library.Where(a => a.CleanName != null).GroupBy(a => a.CleanName).Where(g => g.Count() > 1).Select(g => g.Key)];
            if (duplicates.Count == 0)
                return releases;

            List<ReleaseInfo> kept = new(releases.Count);
            int retitled = 0;
            int dropped = 0;

            foreach (ReleaseInfo release in releases)
            {
                // The same parse Lidarr's DownloadDecisionMaker will do; an unparseable title never reaches FindByName.
                string? clean = ReleaseTitleParser.ParseAlbumTitle(release.Title)?.ArtistName?.CleanArtistName();
                if (clean == null || clean == searched.CleanName || !duplicates.Contains(clean))
                {
                    kept.Add(release);
                    continue;
                }

                if (TryRetitleAs(release, searched))
                {
                    retitled++;
                    kept.Add(release);
                    continue;
                }

                dropped++;
            }

            if (retitled > 0 || dropped > 0)
                logger.Debug("{Indexer}: {Retitled} result(s) retitled and {Dropped} dropped for '{Artist}' — their credited artist matches more than one library artist",
                    indexer, retitled, dropped, searched.Name);

            return kept;
        }

        // Only where the store credits the searched artist as a main artist and the new title parses
        // back to them. Never a Various Artists credit: a compilation is not an ambiguity to resolve.
        private static bool TryRetitleAs(ReleaseInfo release, Artist searched)
        {
            if (release is not StoreReleaseInfo store
                || string.IsNullOrEmpty(release.Artist)
                || StoreReleaseVerifier.IsVariousArtists(release.Artist)
                || release.Title?.StartsWith(release.Artist, StringComparison.Ordinal) != true
                || !store.MainArtists.Any(name => name.CleanArtistName() == searched.CleanName))
                return false;

            string title = searched.Name + release.Title[release.Artist.Length..];
            if (ReleaseTitleParser.ParseAlbumTitle(title)?.ArtistName?.CleanArtistName() != searched.CleanName)
                return false;

            release.Title = title;
            release.Artist = searched.Name;
            return true;
        }
    }
}
