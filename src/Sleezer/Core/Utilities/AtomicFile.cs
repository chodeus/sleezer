namespace NzbDrone.Plugin.Sleezer.Core.Utilities;

/// <summary>Writes a file whole or not at all: written aside and flushed to disk, then moved over the old one.</summary>
public static class AtomicFile
{
    public static void Write(string path, string contents)
    {
        path = Target(path);
        string temp = TempFor(path);
        try
        {
            using (FileStream stream = new(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(System.Text.Encoding.UTF8.GetBytes(contents));
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    public static async Task WriteAsync(string path, string contents)
    {
        path = Target(path);
        string temp = TempFor(path);
        try
        {
            await using (FileStream stream = new(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(contents));
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    // Unique per write: two writers of one path must not share a temp file.
    private static string TempFor(string path) => $"{path}.{Guid.NewGuid():N}.tmp";

    // A symlinked file is written through, as an in-place write would: the move must not replace the link.
    private static string Target(string path)
    {
        FileInfo file = new(path);
        return file.LinkTarget == null ? path : file.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
