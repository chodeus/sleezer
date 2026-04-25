using System;
using System.Collections.Generic;

namespace NzbDrone.Plugin.Sleezer.Core.Tidal
{
    // Pure helper extracted for unit testability; used by Tidal's parser to
    // decide whether a search artist token should be considered a match for
    // a given album's artists list. Handles the compilation/cast cases that
    // upstream TrevTV/Lidarr.Plugin.Tidal misses (issue #21).
    internal static class TidalArtistMatcher
    {
        private static readonly HashSet<string> CompilationArtistMarkers = new(StringComparer.OrdinalIgnoreCase)
        {
            "Various Artists",
            "Various",
            "Soundtrack",
            "Original Cast",
            "Original Cast Recording",
            "Cast Recording",
            "Original Motion Picture Soundtrack",
            "Original Television Soundtrack"
        };

        public static bool ArtistMatches(string searchArtistQuery, IEnumerable<string> albumArtistNames)
        {
            if (string.IsNullOrWhiteSpace(searchArtistQuery))
                return true;

            foreach (var name in albumArtistNames)
            {
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                if (CompilationArtistMarkers.Contains(name))
                    return true;
                if (CompilationArtistMarkers.Contains(searchArtistQuery))
                    return true;
                if (string.Equals(name, searchArtistQuery, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (name.Contains(searchArtistQuery, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (searchArtistQuery.Contains(name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}
