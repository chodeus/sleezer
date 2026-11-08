namespace NzbDrone.Plugin.Sleezer.Core.Utilities;

/// <summary>One small file per download, named by its id and written whole or not at all.</summary>
public static class DownloadIdFile
{
    // The download id names the file; anything beyond letters, digits and dashes could point it elsewhere.
    public static string? PathFor(string folder, string downloadId, string extension) =>
        downloadId.Length > 0 && downloadId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
            ? Path.Combine(folder, downloadId + extension)
            : null;

    public static void Write(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.Write(path, contents);
    }
}
