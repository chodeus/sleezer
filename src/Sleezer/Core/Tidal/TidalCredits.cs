using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace NzbDrone.Plugin.Sleezer.Core.Tidal
{
    /// <summary>Reads per-track composers out of one page of Tidal's albums/{id}/items/credits.</summary>
    public static class TidalCredits
    {
        public static void AddComposers(JToken page, IDictionary<string, string[]> composersByTrack)
        {
            foreach (JToken entry in page["items"] ?? Enumerable.Empty<JToken>())
            {
                string? trackId = entry["item"]?["id"]?.ToString();
                if (!string.Equals(entry["type"]?.ToString(), "track", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(trackId))
                    continue;

                string[] composers = (entry["credits"] ?? Enumerable.Empty<JToken>())
                    .Where(c => string.Equals(c["type"]?.ToString(), "Composer", StringComparison.OrdinalIgnoreCase))
                    .SelectMany(c => c["contributors"] ?? Enumerable.Empty<JToken>())
                    .Select(c => c["name"]?.ToString())
                    .OfType<string>()
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (composers.Length > 0)
                    composersByTrack[trackId] = composers;
            }
        }
    }
}
