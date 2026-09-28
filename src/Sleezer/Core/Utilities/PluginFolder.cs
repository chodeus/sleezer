namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    public static class PluginFolder
    {
        /// <summary>The folder Lidarr installed the plugin into.</summary>
        // Lidarr names it plugins/<owner>/<repo> from the release URL (InstallPluginService), so the casing must come from the repo URL.
        public static string Resolve(string pluginRoot, string repoUrl)
        {
            string[] parts = repoUrl.TrimEnd('/').Split('/');
            return Path.Combine(pluginRoot, parts[^2], parts[^1]);
        }

        /// <summary>Moves a file left under an older, differently cased folder to <paramref name="path"/> and drops that folder once empty.</summary>
        public static bool AdoptLegacyFile(string legacyPath, string path)
        {
            // On a case-insensitive filesystem both paths name one file, and File.Exists(path) is already true.
            if (string.Equals(legacyPath, path, StringComparison.Ordinal) || !File.Exists(legacyPath) || File.Exists(path))
                return false;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Move(legacyPath, path);

            string legacyFolder = Path.GetDirectoryName(legacyPath)!;
            if (!Directory.EnumerateFileSystemEntries(legacyFolder).Any())
                Directory.Delete(legacyFolder);

            return true;
        }

        /// <summary>The file to read and write: <paramref name="path"/>, unless the only copy is still at <paramref name="legacyPath"/>.</summary>
        public static string FileInUse(string legacyPath, string path) =>
            File.Exists(path) || !File.Exists(legacyPath) ? path : legacyPath;
    }
}
