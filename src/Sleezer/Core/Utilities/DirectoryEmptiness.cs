namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    public static class DirectoryEmptiness
    {
        /// <summary>
        /// True only when the directory tree contains no files at all. Enumerates
        /// WITHOUT the default hidden/system attribute mask and WITHOUT ignoring
        /// inaccessible entries, so a dotfile-only folder (.DS_Store, .nomedia,
        /// .keep, .nfsXXXX) or an unreadable subtree counts as non-empty. Fails
        /// closed: any enumeration error returns false (treat as non-empty) so a
        /// destructive caller never deletes on an unproven-empty verdict. Empty
        /// nested subdirectories do NOT keep the tree — only files do.
        /// </summary>
        public static bool IsTreeFileFree(string path)
        {
            try
            {
                return !Directory.EnumerateFiles(path, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    AttributesToSkip = FileAttributes.None,
                    IgnoreInaccessible = false,
                }).Any();
            }
            catch
            {
                return false;
            }
        }
    }
}
