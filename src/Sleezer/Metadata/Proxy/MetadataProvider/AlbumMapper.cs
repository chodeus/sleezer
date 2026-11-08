using DryIoc.ImTools;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Profiles.Metadata;
using System.Globalization;
using System.Text.RegularExpressions;

namespace NzbDrone.Plugin.Sleezer.Metadata.Proxy.MetadataProvider
{
    public static class AlbumMapper
    {
        /// <summary>
        /// Primary type mapping: maps strings to a standard primary album type.
        /// </summary>
        public static readonly Dictionary<string, string> PrimaryTypeMap = new(StringComparer.OrdinalIgnoreCase)
        {
            { "album", "Album" },
            { "broadcast", "Broadcast" },
            { "ep", "EP" },
            { "single", "Single" },
            // Discogs-specific format variations
            { "maxi-single", "Single" },
            { "mini-album", "EP" },
            { "maxisingle", "Single" }
        };

        /// <summary>
        /// Secondary type keywords used for determining types based on title.
        /// </summary>
        public static readonly Dictionary<SecondaryAlbumType, List<string>> SecondaryTypeKeywords = new()
        {
            { SecondaryAlbumType.Live, new List<string> { "live at", "live in", "live from", "in concert", "unplugged", "live performance", "live session", "recorded live", "live", "concert", "tour", "festival", "performance" } },
            // Not remasters or deluxe editions: MusicBrainz, which Lidarr follows, never calls those remix albums.
            { SecondaryAlbumType.Remix, new List<string> { "remixed", "remixes", "remix", "rework", "reimagined", "revisited", "redux" } },
            { SecondaryAlbumType.Compilation, new List<string> { "greatest hits", "best of", "the best of", "very best", "ultimate", "essential", "essentials", "definitive", "collection", "anthology", "retrospective", "complete", "selected works", "treasures", "favorites", "favourites", " hits", "singles collection" } },
            { SecondaryAlbumType.Soundtrack, new List<string> { "original soundtrack", "original motion picture soundtrack", "music from and inspired by", "music from the motion picture", "music from the film", "original score", "film score", "movie soundtrack", "soundtrack", "ost", "music from", "inspired by the" } },
            { SecondaryAlbumType.Spokenword, new List<string> { "spoken word", "poetry reading", "lecture", "speech", "reading", "poetry" } },
            { SecondaryAlbumType.Interview, new List<string> { "interview", "interviews", "in conversation", "q&a", "conversation with" } },
            { SecondaryAlbumType.Audiobook, new List<string> { "audiobook", "audio book", "unabridged", "abridged", "narrated by", "narration" } },
            { SecondaryAlbumType.Demo, new List<string> { "demo", "demos", "rough mix", "rough mixes", "early recordings", "sessions", "outtakes", "alternate takes", "rarities", "unreleased", "bootleg" } },
            { SecondaryAlbumType.Mixtape, new List<string> { "mixtape", "mix tape", "the mixtape" } },
            { SecondaryAlbumType.DJMix, new List<string> { "dj mix", "continuous mix", "mixed by", "mix session", "live mix", "club mix" } },
            { SecondaryAlbumType.Audiodrama, new List<string> { "audio drama", "audio play", "radio play", "radio drama", "theater production", "theatre production" } }
        };

        // Whole words only: a substring match made "Ghost Stories" a soundtrack ("ost") and "Olive" live,
        // and a metadata profile allowing only Studio then hid the album. Declared after the keywords it reads.
        private static readonly Dictionary<SecondaryAlbumType, Regex> SecondaryTypePatterns = SecondaryTypeKeywords.ToDictionary(
            kvp => kvp.Key,
            kvp => new Regex(@"(?<![\p{L}\p{N}])(?:" + string.Join("|", kvp.Value.Select(k => Regex.Escape(k.Trim()))) + @")(?![\p{L}\p{N}])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled));

        /// <summary>
        /// Direct mapping of strings to SecondaryAlbumType (used by Discogs).
        /// </summary>
        public static readonly Dictionary<string, SecondaryAlbumType> SecondaryTypeMap = new(StringComparer.OrdinalIgnoreCase)
        {
            { "compilation", SecondaryAlbumType.Compilation },
            { "studio", SecondaryAlbumType.Studio },
            { "soundtrack", SecondaryAlbumType.Soundtrack },
            { "spokenword", SecondaryAlbumType.Spokenword },
            { "interview", SecondaryAlbumType.Interview },
            { "live", SecondaryAlbumType.Live },
            { "remix", SecondaryAlbumType.Remix },
            { "dj mix", SecondaryAlbumType.DJMix },
            { "mixtape", SecondaryAlbumType.Mixtape },
            { "demo", SecondaryAlbumType.Demo },
            { "audio drama", SecondaryAlbumType.Audiodrama },
            { "master", new() { Id = 36, Name = "Master" } },
            { "release", new() { Id = 37, Name = "Release" } },
        };

        /// <summary>
        /// Extracts a link name from a URL.
        /// </summary>
        /// <param name="url">The URL to extract the link name from.</param>
        /// <returns>A human-readable name for the link (e.g., "Bandcamp", "YouTube").</returns>
        public static string GetLinkNameFromUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return "Website";

            try
            {
                Uri uri = new(url);
                string[] hostParts = uri.Host.Split('.', StringSplitOptions.RemoveEmptyEntries);

                if (hostParts.Contains("bandcamp"))
                    return "Bandcamp";
                if (hostParts.Contains("facebook"))
                    return "Facebook";
                if (hostParts.Contains("youtube"))
                    return "YouTube";
                if (hostParts.Contains("soundcloud"))
                    return "SoundCloud";
                if (hostParts.Contains("discogs"))
                    return "Discogs";

                string mainDomain = hostParts.Length > 1 ? hostParts[^2] : hostParts[0];
                return mainDomain.ToUpper(CultureInfo.InvariantCulture);
            }
            catch { return "Website"; }
        }

        /// <summary>
        /// Determines secondary album types from a title using keyword matching.
        /// </summary>
        /// <param name="title">The title of the album to analyze.</param>
        /// <returns>A list of detected secondary album types. Returns empty list if title is null or whitespace.</returns>
        public static List<SecondaryAlbumType> DetermineSecondaryTypesFromTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return [];

            string? cleanTitle = Parser.NormalizeTitle(title)?.ToLowerInvariant();
            if (cleanTitle == null)
                return [];

            HashSet<SecondaryAlbumType> detectedTypes = [];
            foreach (KeyValuePair<SecondaryAlbumType, Regex> kvp in SecondaryTypePatterns)
            {
                if (kvp.Value.IsMatch(title) || kvp.Value.IsMatch(cleanTitle))
                    detectedTypes.Add(kvp.Key);
            }

            if (detectedTypes.Contains(SecondaryAlbumType.Live) && detectedTypes.Contains(SecondaryAlbumType.Remix))
                detectedTypes.Remove(SecondaryAlbumType.Remix);

            return [.. detectedTypes];
        }

        /// <summary>
        /// Filters albums based on a metadata profile.
        /// </summary>
        /// <param name="albums">The collection of albums to filter.</param>
        /// <param name="metadataProfileId">The ID of the metadata profile to use for filtering.</param>
        /// <param name="metadataProfileService">The service to retrieve metadata profiles.</param>
        /// <returns>A filtered collection of albums that match the metadata profile criteria.</returns>
        public static List<Album> FilterAlbums(IEnumerable<Album> albums, int metadataProfileId, IMetadataProfileService metadataProfileService)
        {
            MetadataProfile metadataProfile = metadataProfileService.Exists(metadataProfileId) ? metadataProfileService.Get(metadataProfileId) : metadataProfileService.All().First();
            List<string> primaryTypes = new(metadataProfile.PrimaryAlbumTypes.Where(s => s.Allowed).Select(s => s.PrimaryAlbumType.Name));
            List<string> secondaryTypes = new(metadataProfile.SecondaryAlbumTypes.Where(s => s.Allowed).Select(s => s.SecondaryAlbumType.Name));
            List<string> releaseStatuses = new(metadataProfile.ReleaseStatuses.Where(s => s.Allowed).Select(s => s.ReleaseStatus.Name));

            NzbDroneLogger.GetLogger(typeof(AlbumMapper)).Trace($"Metadata Profile allows: Primary Types: {string.Join(", ", primaryTypes)} | Secondary Types: {string.Join(", ", secondaryTypes)} |  Release Statuses: {string.Join(", ", releaseStatuses)}");
            return albums.Where(album => primaryTypes.Contains(album.AlbumType) &&
                                ((album.SecondaryTypes.Count == 0 && secondaryTypes.Contains("Studio")) ||
                                 album.SecondaryTypes.Any(x => secondaryTypes.Contains(x.Name))) &&
                                album.AlbumReleases.Value.Any(x => releaseStatuses.Contains(x.Status))).ToList();
        }

        /// <summary>
        /// Maps album types based on a collection of format description strings.
        /// Sets both the primary album type and the secondary types.
        /// </summary>
        /// <param name="formatDescriptions">The collection of format descriptions to analyze.</param>
        /// <param name="album">The album object to update with the mapped types.</param>
        public static void MapAlbumTypes(IEnumerable<string>? formatDescriptions, Album album)
        {
            album.AlbumType = "Album";
            if (formatDescriptions != null)
            {
                foreach (string desc in formatDescriptions)
                {
                    if (PrimaryTypeMap.TryGetValue(desc.ToLowerInvariant(), out string? primaryType))
                    {
                        album.AlbumType = primaryType;
                        break;
                    }
                }

                HashSet<SecondaryAlbumType> secondaryTypes = [];
                foreach (string desc in formatDescriptions)
                {
                    if (SecondaryTypeMap.TryGetValue(desc.ToLowerInvariant(), out SecondaryAlbumType? secondaryType))
                        secondaryTypes.Add(secondaryType);
                }
                album.SecondaryTypes = [.. secondaryTypes];
            }
        }
    }
}