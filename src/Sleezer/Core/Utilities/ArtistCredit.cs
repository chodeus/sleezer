using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using FuzzySharp;

namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    /// <summary>Whether a credit names an artist: loosely for a filter, or outright for a check that decides whose file it is.</summary>
    public sealed partial class ArtistCredit
    {
        private const int FuzzyFloor = 90;

        private readonly string _normalized;
        private readonly string[] _tokens;
        private readonly HashSet<string> _names;
        private readonly HashSet<string> _joinedNames;

        public ArtistCredit(string? name, IEnumerable<string?>? aliases = null)
        {
            Name = name ?? string.Empty;
            IsVariousArtists = StoreReleaseVerifier.IsVariousArtists(name);
            _normalized = StoreReleaseVerifier.Normalize(name);
            _tokens = _normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            _names = [.. (aliases ?? []).Prepend(name).Select(StoreReleaseVerifier.Normalize).Where(n => n.Length > 0)];
            _joinedNames = [.. _names.Select(Joined)];
        }

        public string Name { get; }

        public bool IsVariousArtists { get; }

        /// <summary>False when neither the name nor an alias has anything to compare.</summary>
        public bool IsJudgeable => _names.Count > 0;

        /// <summary>Whether the credit names the artist: the name or an alias, the artist's words inside a collaboration credit, or a near spelling.</summary>
        public bool IsNamedBy(string? credit)
        {
            string normalized = StoreReleaseVerifier.Normalize(credit);
            if (normalized.Length == 0)
                return false;

            if (IsVariousArtists)
                return StoreReleaseVerifier.IsVariousArtists(credit);

            if (IsName(normalized))
                return true;

            // A collaboration credit ("Afrojack & David Guetta") still contains the artist whole.
            HashSet<string> tokens = [.. normalized.Split(' ')];
            if (_tokens.Length > 0 && _tokens.All(tokens.Contains))
                return true;

            return Fuzz.TokenSortRatio(normalized, _normalized) >= FuzzyFloor;
        }

        /// <summary>Whether the credit lists the artist outright: the whole credit, or a run of its ";" or "/" parts, is the name or an alias.</summary>
        // "Ember Falls" is not "Ember", and "&" or "," stays inside a name ("Simon & Garfunkel").
        public bool IsListedIn(string? credit)
        {
            if (IsVariousArtists)
                return StoreReleaseVerifier.IsVariousArtists(credit);

            // Odd indexes hold the separators, so every run starts and ends on a part.
            string[] pieces = ListSeparatorRegex().Split(credit ?? string.Empty);
            for (int first = 0; first < pieces.Length; first += 2)
            {
                for (int last = first; last < pieces.Length; last += 2)
                {
                    if (IsName(StoreReleaseVerifier.Normalize(string.Concat(pieces[first..(last + 1)]))))
                        return true;
                }
            }

            return false;
        }

        // "MKRa" is "MK Ra" written without its word break.
        private bool IsName(string normalized) =>
            normalized.Length > 0 && (_names.Contains(normalized) || _joinedNames.Contains(Joined(normalized)));

        private static string Joined(string normalized) => normalized.Replace(" ", string.Empty);

        [GeneratedRegex(@"(\s*[;/]\s*)")]
        private static partial Regex ListSeparatorRegex();
    }
}
