namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    /// <summary>The folder a store download writes an album to.</summary>
    public static class DownloadFolders
    {
        // Keep the id: removing a finished download deletes its whole folder.
        public static string WithStoreId(string albumDirectory, string storeId) =>
            $"{albumDirectory.TrimEnd('/', '\\')} [{storeId}]{Path.DirectorySeparatorChar}";
    }
}
