using NzbDrone.Core.History;

namespace NzbDrone.Plugin.Sleezer.Metadata.ScheduledTasks.SearchSniper
{
    /// <summary>The grab each library file came from: its import row records the file id, and the grab shares that row's download id.</summary>
    public static class ImportGrabs
    {
        public static Dictionary<int, string> DownloadIds(IEnumerable<int> fileIds, IEnumerable<EntityHistory> imports)
        {
            HashSet<int> wanted = [.. fileIds];
            return imports
                .Where(i => !string.IsNullOrWhiteSpace(i.DownloadId) && FileId(i) is int id && wanted.Contains(id))
                .GroupBy(i => FileId(i)!.Value)
                .ToDictionary(g => g.Key, g => g.MaxBy(i => i.Date)!.DownloadId);
        }

        public static Dictionary<int, EntityHistory> Of(Dictionary<int, string> downloads, IEnumerable<EntityHistory> grabs)
        {
            Dictionary<string, EntityHistory> latest = grabs
                .GroupBy(g => g.DownloadId)
                .ToDictionary(g => g.Key, g => g.MaxBy(h => h.Date)!);

            return downloads
                .Where(d => latest.ContainsKey(d.Value))
                .ToDictionary(d => d.Key, d => latest[d.Value]);
        }

        // Lidarr writes the key as FileId; the stored JSON camel-cases it.
        private static int? FileId(EntityHistory import) =>
            import.Data?.FirstOrDefault(kv => kv.Key.Equals("fileId", StringComparison.OrdinalIgnoreCase)).Value is { } value && int.TryParse(value, out int id) ? id : null;
    }
}
