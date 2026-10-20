using System;
using System.Collections.Generic;
using System.Linq;
using FuzzySharp;

namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    /// <summary>Whether a credit names an artist: its name or an alias, its words inside a collaboration credit, or a near spelling.</summary>
    public sealed class ArtistCredit
    {
        private const int FuzzyFloor = 90;

        private readonly string _normalized;
        private readonly string[] _tokens;
        private readonly HashSet<string> _aliases;

        public ArtistCredit(string? name, IEnumerable<string?>? aliases = null)
        {
            Name = name ?? string.Empty;
            IsVariousArtists = StoreReleaseVerifier.IsVariousArtists(name);
            _normalized = StoreReleaseVerifier.Normalize(name);
            _tokens = _normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            _aliases = [.. (aliases ?? []).Select(StoreReleaseVerifier.Normalize).Where(a => a.Length > 0)];
        }

        public string Name { get; }

        public bool IsVariousArtists { get; }

        /// <summary>Whether the credit names the artist; a blank credit names no one, and only a Various Artists credit names Various Artists.</summary>
        public bool IsNamedBy(string? credit)
        {
            string normalized = StoreReleaseVerifier.Normalize(credit);
            if (normalized.Length == 0)
                return false;

            if (IsVariousArtists)
                return StoreReleaseVerifier.IsVariousArtists(credit);

            if (normalized == _normalized || _aliases.Contains(normalized))
                return true;

            // A collaboration credit ("Afrojack & David Guetta") still contains the artist whole.
            HashSet<string> tokens = [.. normalized.Split(' ')];
            if (_tokens.Length > 0 && _tokens.All(tokens.Contains))
                return true;

            return Fuzz.TokenSortRatio(normalized, _normalized) >= FuzzyFloor;
        }
    }
}
