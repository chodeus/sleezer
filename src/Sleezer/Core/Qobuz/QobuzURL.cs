using System.Text.RegularExpressions;

namespace NzbDrone.Plugin.Sleezer.Core.Qobuz
{
    public enum QobuzEntityType
    {
        Track,
        Playlist,
        Album,
        Artist,
        Label,
        User
    }

    /// <summary>Parses the album/track identity out of a Qobuz store or player URL.</summary>
    public class QobuzURL(string url, QobuzEntityType type, string id)
    {
        // Each pattern is anchored and segment-counted (2, 5, 4), so they're mutually
        // exclusive and order doesn't matter.
        private static readonly Regex[] UrlPatterns =
        [
            new(@"^https?://(?:.*?\.)?qobuz\.com/(?<type>[^/]+?)/(?<id>[^/]+?)/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new(@"^https?://(?:.*?\.)?qobuz\.com/[^/]+/(?<type>[^/]+?)/[^/]+/download-streaming-albums/(?<id>[^/]+?)/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new(@"^https?://(?:.*?\.)?qobuz\.com/[^/]+/(?<type>[^/]+?)/[^/]+/(?<id>[^/]+?)/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled)
        ];

        // "interpreter" is Qobuz's word for an artist page in store links.
        private static readonly Dictionary<string, QobuzEntityType> LinkTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["album"] = QobuzEntityType.Album,
            ["track"] = QobuzEntityType.Track,
            ["artist"] = QobuzEntityType.Artist,
            ["interpreter"] = QobuzEntityType.Artist,
            ["label"] = QobuzEntityType.Label,
            ["user"] = QobuzEntityType.User,
            ["playlist"] = QobuzEntityType.Playlist,
        };

        public string Url { get; init; } = url;

        public QobuzEntityType EntityType { get; init; } = type;

        public string Id { get; init; } = id;

        public static bool TryParse(string url, out QobuzURL? qobuzUrl)
        {
            qobuzUrl = null;

            if (string.IsNullOrWhiteSpace(url))
                return false;

            int paramStart = url.IndexOf('?');
            if (paramStart != -1)
                url = url[..paramStart];

            foreach (Regex pattern in UrlPatterns)
            {
                Match match = pattern.Match(url);
                if (!match.Success)
                    continue;

                // Matched against names only — Enum.TryParse would otherwise accept a
                // numeric path segment like "/2/" as QobuzEntityType.Album.
                if (!LinkTypes.TryGetValue(match.Groups["type"].Value, out QobuzEntityType parsedType))
                    continue;

                string id = match.Groups["id"].Value;
                if (string.IsNullOrEmpty(id))
                    continue;

                qobuzUrl = new QobuzURL(url, parsedType, id);
                return true;
            }

            return false;
        }
    }
}
