using System.Text;

namespace NzbDrone.Plugin.Sleezer.Notifications.PlaylistExport;

public static class PlaylistFile
{
    // M3U8 is UTF-8 by definition; a BOM puts bytes before the #EXTM3U header.
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>Writes the tracks that exist on disk; returns how many, writing nothing when none do.</summary>
    public static int Write(string fullPath, string listName, IEnumerable<string> trackPaths, string outputPath, bool useRelative)
    {
        List<string> present = [.. trackPaths.Where(File.Exists)];
        if (present.Count == 0)
            return 0;

        List<string> lines = ["#EXTM3U", $"#PLAYLIST:{listName}"];
        foreach (string path in present)
        {
            lines.Add($"#EXTINF:-1,{Path.GetFileNameWithoutExtension(path)}");
            lines.Add(useRelative ? Path.GetRelativePath(outputPath, path) : path);
        }

        File.WriteAllLines(fullPath, lines, Utf8NoBom);
        return present.Count;
    }
}
