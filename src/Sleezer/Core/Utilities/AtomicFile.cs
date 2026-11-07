namespace NzbDrone.Plugin.Sleezer.Core.Utilities;

/// <summary>Writes a file whole or not at all: written aside, then moved over the old one.</summary>
public static class AtomicFile
{
    public static void Write(string path, string contents)
    {
        path = Target(path);
        string temp = TempFor(path);
        try
        {
            File.WriteAllText(temp, contents);
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
            await File.WriteAllTextAsync(temp, contents);
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
