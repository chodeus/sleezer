using System;
using System.Collections.Generic;
using System.Linq;

namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    /// <summary>Writes the ISRC, copyright and composers a store reports; a value the store lacks leaves the tag untouched.</summary>
    public static class StoreCreditTags
    {
        // The first non-blank copyright wins, so callers pass the track's before the album's.
        public static void Apply(TagLib.Tag tag, string? isrc, IEnumerable<string?> copyrights, IEnumerable<string?>? composers)
        {
            if (!string.IsNullOrWhiteSpace(isrc))
                tag.ISRC = isrc.Trim();

            string? copyright = copyrights.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
            if (copyright != null)
                tag.Copyright = copyright.Trim();

            string[] names = (composers ?? [])
                .OfType<string>()
                .Select(n => n.Trim())
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (names.Length > 0)
                tag.Composers = names;
        }
    }
}
