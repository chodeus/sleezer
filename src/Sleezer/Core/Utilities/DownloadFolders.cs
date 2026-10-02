namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    /// <summary>The folder a store download writes an album to.</summary>
    public static class DownloadFolders
    {
        // Two releases with one title (an album and its title single) would otherwise share a folder,
        // fail each other's import, and lose files when either finished download is removed.
        public static string WithStoreId(string albumDirectory, string storeId) =>
            $"{albumDirectory.TrimEnd('/', '\\')} [{storeId}]{Path.DirectorySeparatorChar}";
    }
}
