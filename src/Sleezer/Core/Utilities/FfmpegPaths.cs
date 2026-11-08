namespace NzbDrone.Plugin.Sleezer.Core.Utilities;

/// <summary>Paths ffmpeg can be handed: Xabe wraps a path in quotes and never escapes a quote inside it.</summary>
public static class FfmpegPaths
{
    private static string AliasFolder => Path.Combine(Path.GetTempPath(), "sleezer-ffmpeg");

    /// <summary>The path itself, or a quote-free link to it; disposing removes the link.</summary>
    public static FfmpegInput Input(string path)
    {
        if (!path.Contains('"'))
            return new FfmpegInput(path, null);

        Directory.CreateDirectory(AliasFolder);
        string alias = Path.Combine(AliasFolder, Guid.NewGuid().ToString("N") + Path.GetExtension(path).Replace("\"", string.Empty));
        try
        {
            File.CreateSymbolicLink(alias, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Windows without developer mode can't make links; a copy reads the same.
            File.Copy(path, alias);
        }

        return new FfmpegInput(alias, alias);
    }

    /// <summary>Where ffmpeg writes a temporary output for <paramref name="trackPath"/>: beside it, or in the temp folder when the path holds a quote.</summary>
    public static string TempOutput(string trackPath, string suffix)
    {
        if (!trackPath.Contains('"'))
            return Path.ChangeExtension(trackPath, suffix);

        Directory.CreateDirectory(AliasFolder);
        return Path.Combine(AliasFolder, Guid.NewGuid().ToString("N") + suffix);
    }
}

/// <summary>A path for ffmpeg to read; disposing removes the link made for it, if any.</summary>
public sealed class FfmpegInput(string path, string? alias) : IDisposable
{
    public string Path { get; } = path;

    public void Dispose()
    {
        if (alias == null)
            return;

        try
        {
            File.Delete(alias);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover link in the temp folder is harmless.
        }
    }
}
