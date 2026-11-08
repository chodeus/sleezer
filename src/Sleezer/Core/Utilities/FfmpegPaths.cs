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

    /// <summary>Where ffmpeg writes a temporary output for <paramref name="trackPath"/>: always beside it, under a quote-free name when needed.</summary>
    public static string TempOutput(string trackPath, string suffix)
    {
        if (!trackPath.Contains('"'))
            return Path.ChangeExtension(trackPath, suffix);

        // Beside the track, never the temp folder: callers delete the original before moving the output in, and a move
        // across disks is a copy that can fail with the original already gone.
        string folder = Path.GetDirectoryName(trackPath) ?? string.Empty;
        if (folder.Contains('"'))
            throw new InvalidOperationException($"ffmpeg can't be given a folder whose name holds a double quote: {folder}");

        // Named from the track, not at random: callers clear a leftover from an aborted attempt by this name.
        string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(trackPath)))[..16].ToLowerInvariant();
        return Path.Combine(folder, $"sleezer-{key}{suffix}");
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
