using System.Text.RegularExpressions;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;

/// <summary>
/// Resolves where slskd places a transfer locally when
/// transfers.download.destination.subdirectory is configured to a non-default
/// pattern. Ported from TypNull/Tubifarry d857a0f.
/// </summary>
public static partial class SlskdPathResolver
{
    public static string? ResolveSubdirectory(
        SlskdDestinationConfig config, string username, string remoteFilename, string? batchId = null, string? externalId = null)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(remoteFilename))
            return null;

        string pattern = config.SubdirectoryPattern ?? SlskdDestinationConfig.DefaultPattern;
        if (pattern.Equals("{}", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        string[] sourceSegments = SplitSegments(StripRoot(remoteFilename)).SkipLast(1).ToArray();
        string sourcePath = string.Join('/', sourceSegments);
        string sourceDirectory = sourceSegments.LastOrDefault() ?? string.Empty;

        Dictionary<string, string> tokens = new(StringComparer.OrdinalIgnoreCase)
        {
            ["SOURCE_USERNAME"] = username,
            ["SOURCE_PATH"] = sourcePath,
            ["SOURCE_DIRECTORY"] = sourceDirectory,
            ["BATCH_ID"] = batchId ?? "unknown_batch_id",
            ["BATCH_EXTERNAL_ID"] = externalId ?? "unknown_batch_external_id",
            ["SEARCH_ID"] = "unknown_search_id",
            ["SEARCH_TEXT"] = "unknown_search_text",
        };

        string destination = TokenRegex().Replace(pattern, match =>
            tokens.TryGetValue(match.Groups[1].Value, out string? value) ? value : match.Value);

        string[] segments = SplitSegments(destination);
        if (segments.Any(s => s is "." or ".."))
            return null;

        return string.Join('/', segments);
    }

    /// <summary>
    /// Turns slskd's absolute localDirectoryName (from DownloadDirectoryComplete
    /// events) into a path relative to the downloads directory; null when the
    /// path is outside it.
    /// </summary>
    public static string? MakeRelativeToDownloads(string downloadsDirectory, string? absolutePath)
    {
        if (string.IsNullOrWhiteSpace(downloadsDirectory) || string.IsNullOrWhiteSpace(absolutePath))
            return null;

        string root = Normalize(downloadsDirectory).TrimEnd('/');
        string path = Normalize(absolutePath).TrimEnd('/');

        if (path.Equals(root, StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        if (!path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
            return null;

        string relative = path[(root.Length + 1)..];

        // The event payload is not fully under our control — fail closed on
        // relative segments ("root//../x" passes the prefix check above).
        string[] segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s is "." or ".."))
            return null;

        return string.Join('/', segments);
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static string[] SplitSegments(string path) =>
        Normalize(path).Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static string StripRoot(string path)
    {
        path = Normalize(path);
        path = DriveRootRegex().Replace(path, string.Empty);
        path = UncRootRegex().Replace(path, string.Empty);
        path = SoulseekQtRootRegex().Replace(path, string.Empty);
        return path;
    }

    [GeneratedRegex(@"\$\{([^\{\}]*)\}", RegexOptions.IgnoreCase)]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"^[a-zA-Z]:/?")]
    private static partial Regex DriveRootRegex();

    [GeneratedRegex(@"^//[^/]+/?")]
    private static partial Regex UncRootRegex();

    [GeneratedRegex(@"^@@[a-zA-Z0-9]{5,}/?")]
    private static partial Regex SoulseekQtRootRegex();
}
