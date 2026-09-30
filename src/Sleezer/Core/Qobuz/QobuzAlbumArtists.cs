namespace NzbDrone.Plugin.Sleezer.Core.Qobuz
{
    /// <summary>Qobuz lists featured artists alongside the main ones; these pick the main ones.</summary>
    public static class QobuzAlbumArtists
    {
        public static List<string> Main(IEnumerable<(string? Name, IReadOnlyCollection<string>? Roles)> artists) =>
            [.. artists
                .Where(a => a.Name != null && a.Roles?.Any(r => string.Equals(r, "main-artist", StringComparison.OrdinalIgnoreCase)) == true)
                .Select(a => a.Name!)];

        // For tags: without roles there is nothing to tell them apart, so every artist is kept as before.
        public static string[] ForTags(IEnumerable<(string? Name, IReadOnlyCollection<string>? Roles)> artists)
        {
            var named = artists.Where(a => !string.IsNullOrWhiteSpace(a.Name)).ToList();
            List<string> main = Main(named);
            return main.Count > 0 ? [.. main] : [.. named.Select(a => a.Name!)];
        }
    }
}
