using System.Text;

namespace NzbDrone.Plugin.Sleezer.Core.PostProcessing;

public record CorruptionStrike(string File, string? Reason);

/// <summary>Builds the failure message Lidarr shows in History and the Blocklist.</summary>
public static class CorruptionReport
{
    // Lidarr sends this text to notifications: Pushover's limit is 1,024 characters, Discord's 2,000.
    public const int MaxMessageLength = 1000;

    /// <summary>Lists the failed files by name until the message is full, then counts the rest.</summary>
    public static string FailureMessage(IReadOnlyList<CorruptionStrike> strikes)
    {
        List<CorruptionStrike> byName = [.. strikes.OrderBy(s => s.File, StringComparer.OrdinalIgnoreCase)];
        string count = byName.Count == 1 ? "1 file" : $"{byName.Count} files";
        StringBuilder message = new($"Sleezer: {count} failed the corruption check — ");
        for (int i = 0; i < byName.Count; i++)
        {
            string entry = $"{(i > 0 ? "; " : "")}{Describe(byName[i])}";
            string rest = MoreSuffix(byName.Count - i - 1);
            if (message.Length + entry.Length + rest.Length > MaxMessageLength)
            {
                if (i > 0)
                    return message.Append(MoreSuffix(byName.Count - i)).ToString();
                int cut = MaxMessageLength - message.Length - rest.Length - 1;
                if (char.IsHighSurrogate(entry[cut - 1]))
                    cut--;
                entry = entry[..cut] + "…";
            }

            message.Append(entry);
        }

        return message.ToString();
    }

    /// <summary>One failed file and why, as the message and the log show it.</summary>
    public static string Describe(CorruptionStrike strike) => $"{strike.File}: {strike.Reason ?? "no reason recorded"}";

    /// <summary>The file's path inside the download folder, so a disc folder (CD1/, CD2/) stays in the name.</summary>
    public static string FileLabel(string folder, string path) => Path.GetRelativePath(folder, path);

    private static string MoreSuffix(int left) => left > 0 ? $"; +{left} more (see the Lidarr log)" : string.Empty;
}
