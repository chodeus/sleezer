using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    /// <summary>The searched artist for one search, readable from Lidarr's tier loop.</summary>
    // Lidarr's IsValidRelease sees only the release, so the search's context rides an AsyncLocal:
    // it flows down that search's own awaits and never into a concurrent search on the same indexer.
    public sealed class AmbiguousArtistScope : IDisposable
    {
        private static readonly AsyncLocal<AmbiguousArtistScope?> Current = new();

        private readonly AmbiguousArtistScope? _outer;
        private readonly Artist _searched;
        private readonly HashSet<string> _duplicates;

        private AmbiguousArtistScope(Artist searched, HashSet<string> duplicates)
        {
            _outer = Current.Value;
            _searched = searched;
            _duplicates = duplicates;
            Current.Value = this;
        }

        public static IDisposable Begin(Artist? searched, Func<IEnumerable<Artist>> library)
        {
            if (string.IsNullOrWhiteSpace(searched?.CleanName))
                return new AmbiguousArtistScope(new Artist(), []);

            return new AmbiguousArtistScope(searched, AmbiguousArtistGuard.DuplicatedCleanNames(library()));
        }

        /// <summary>True when the current search would drop this result for its ambiguous artist.</summary>
        public static bool Rejects(ReleaseInfo release) =>
            Current.Value is { _duplicates.Count: > 0 } scope && AmbiguousArtistGuard.Rejects(release, scope._searched, scope._duplicates);

        public void Dispose() => Current.Value = _outer;
    }
}
