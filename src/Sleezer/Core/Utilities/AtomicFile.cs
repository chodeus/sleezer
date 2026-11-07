namespace NzbDrone.Plugin.Sleezer.Core.Utilities;

/// <summary>Writes a file whole or not at all: written aside, then moved over the old one.</summary>
public static class AtomicFile
{
    public static void Write(string path, string contents)
    {
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

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
