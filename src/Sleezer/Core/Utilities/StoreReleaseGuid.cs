using System.Text.RegularExpressions;

namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    /// <summary>The Guid of a Qobuz, Deezer or Tidal release: the store, its album id, then the quality tier on offer.</summary>
    public static class StoreReleaseGuid
    {
        public const string Qobuz = "Qobuz";
        public const string Deezer = "Deezer";
        public const string Tidal = "Tidal";

        // Lidarr prefixes "<indexer id>_" in IndexerBase.CleanupReleases, so the album key keeps its indexer.
        private static readonly Regex Shape = new($@"^(?<album>(?:\d+_)?(?:{Qobuz}|{Deezer}|{Tidal})-[^-]+)-[^-]+$", RegexOptions.Compiled);

        public static string Create(string store, object albumId, object tier) => $"{store}-{albumId}-{tier}";

        /// <summary>The Guid without its quality tier, or null when it is not a store release.</summary>
        public static string? AlbumKey(string? guid)
        {
            if (string.IsNullOrEmpty(guid))
                return null;

            Match match = Shape.Match(guid);
            return match.Success ? match.Groups["album"].Value : null;
        }
    }
}
