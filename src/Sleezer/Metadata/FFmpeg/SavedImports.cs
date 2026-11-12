using System.Runtime.CompilerServices;

namespace NzbDrone.Plugin.Sleezer.Metadata.FFmpeg
{
    /// <summary>A saved track file is an import only when it is the very object Lidarr announced as added.</summary>
    public static class SavedImports
    {
        // Weak: a file a scan adds is announced too, never asked about, and collected
        private static readonly ConditionalWeakTable<object, object> JustSaved = new();

        public static void Saved(object trackFile) => JustSaved.AddOrUpdate(trackFile, trackFile);

        /// <summary>True once for a file being imported: one not saved yet, or the object just saved.</summary>
        public static bool IsImport(object trackFile, int id) => JustSaved.Remove(trackFile) || id == 0;
    }
}
