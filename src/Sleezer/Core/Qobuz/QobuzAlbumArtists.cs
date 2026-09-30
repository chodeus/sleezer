namespace NzbDrone.Plugin.Sleezer.Core.Qobuz
{
    /// <summary>The album artists to tag: Qobuz lists featured artists alongside the main ones.</summary>
    public static class QobuzAlbumArtists
    {
        public static string[] MainNames(IEnumerable<(string? Name, IReadOnlyCollection<string>? Roles)> artists)
        {
            var named = artists.Where(a => !string.IsNullOrWhiteSpace(a.Name)).ToList();
            var main = named.Where(a => a.Roles?.Contains("main-artist") == true).ToList();

            // Without roles there is nothing to tell them apart, so keep every artist as before.
            return [.. (main.Count > 0 ? main : named).Select(a => a.Name!)];
        }
    }
}
