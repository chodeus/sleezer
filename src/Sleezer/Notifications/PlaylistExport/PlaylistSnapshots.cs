using NzbDrone.Plugin.Sleezer.Core.Model;

namespace NzbDrone.Plugin.Sleezer.Notifications.PlaylistExport;

public static class PlaylistSnapshots
{
    /// <summary>
    /// The stored snapshot while it is fresh, else a new fetch. An empty or failed
    /// (null) fetch keeps the stored items so one bad response can't blank a playlist.
    /// </summary>
    public static PlaylistSnapshot? Resolve(
        PlaylistSnapshot? stored,
        TimeSpan minRefreshInterval,
        DateTime utcNow,
        string listName,
        Func<List<PlaylistItem>?> fetch)
    {
        if (stored != null && utcNow - stored.FetchedAt < minRefreshInterval)
            return stored;

        List<PlaylistItem>? items = fetch();
        if (items == null || (items.Count == 0 && stored != null))
            return stored;

        return new PlaylistSnapshot(listName, items, utcNow);
    }
}
