namespace NzbDrone.Plugin.Sleezer.Indexers.Soulseek
{
    /// <summary>
    /// slskd URLs carried on a release. The peer link is what Lidarr displays;
    /// the search link is identity, and interactive-grab cleanup matches on it.
    /// </summary>
    public static class SlskdUrls
    {
        /// <summary>slskd's browse page for a peer — the release's display link.</summary>
        public static string Peer(SlskdSettings? settings, string? username) =>
            Host(settings) is { Length: > 0 } host && !string.IsNullOrEmpty(username)
                ? $"{host}/browse?user={Uri.EscapeDataString(username)}"
                : "";

        /// <summary>The search a release came from.</summary>
        public static string Search(SlskdSettings? settings, string? searchId) =>
            Host(settings) is { Length: > 0 } host && !string.IsNullOrEmpty(searchId)
                ? $"{host}/searches/{searchId}"
                : "";

        /// <summary>True when a release came from the given search.</summary>
        public static bool IsFromSearch(string? commentUrl, string? searchId) =>
            !string.IsNullOrEmpty(commentUrl) &&
            !string.IsNullOrEmpty(searchId) &&
            commentUrl.EndsWith($"/searches/{searchId}", StringComparison.Ordinal);

        // No host configured yields "" rather than a relative URL, which Lidarr
        // would render against its own address.
        private static string Host(SlskdSettings? settings)
        {
            if (settings == null)
                return "";

            string? host = string.IsNullOrEmpty(settings.ExternalUrl) ? settings.BaseUrl : settings.ExternalUrl;
            return host?.TrimEnd('/') ?? "";
        }
    }
}
