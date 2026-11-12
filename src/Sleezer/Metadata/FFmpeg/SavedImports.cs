using System.Runtime.CompilerServices;

namespace NzbDrone.Plugin.Sleezer.Metadata.FFmpeg
{
    /// <summary>
    /// Tells the track file an import is handing over from one an artist scan read back.
    /// A Lidarr build that saves an import's track files before its metadata pass passes
    /// that pass the objects it just saved; a scan loads its own from the database. So a
    /// saved file is an import only when it is the very object Lidarr announced as added.
    /// Held weakly: a file added by a scan is announced too, never asked about, and collected.
    /// </summary>
    public static class SavedImports
    {
        private static readonly ConditionalWeakTable<object, object> JustSaved = new();

        public static void Saved(object trackFile) => JustSaved.AddOrUpdate(trackFile, trackFile);

        /// <summary>True once for a file being imported: one not saved yet, or the object just saved.</summary>
        public static bool IsImport(object trackFile, int id) => JustSaved.Remove(trackFile) || id == 0;
    }
}
