using System.Text;

namespace NzbDrone.Plugin.Sleezer.Core.PostProcessing;

public record CorruptionStrike(string File, string? Reason);

/// <summary>Builds the failure message Lidarr shows in History and the Blocklist.</summary>
public static class CorruptionReport
{
    // Lidarr sends this text to notifications: Pushover's limit is 1,024 characters, Discord's 2,000.
    public const int MaxMessageLength = 1000;

    /// <summary>Lists each failed file in scan order until the message is full, then counts the rest.</summary>
    public static string FailureMessage(IReadOnlyList<CorruptionStrike> strikes)
    {
        string count = strikes.Count == 1 ? "1 file" : $"{strikes.Count} files";
        StringBuilder message = new($"Sleezer: {count} failed the corruption check — ");
        for (int i = 0; i < strikes.Count; i++)
        {
            string entry = $"{(i > 0 ? "; " : "")}{strikes[i].File}: {strikes[i].Reason ?? "no reason recorded"}";
            string rest = MoreSuffix(strikes.Count - i - 1);
            if (message.Length + entry.Length + rest.Length > MaxMessageLength)
            {
                if (i > 0)
                    return message.Append(MoreSuffix(strikes.Count - i)).ToString();
                entry = entry[..(MaxMessageLength - message.Length - rest.Length - 1)] + "…";
            }

            message.Append(entry);
        }

        return message.ToString();
    }

    /// <summary>The file's path inside the download folder, so a disc folder (CD1/, CD2/) stays in the name.</summary>
    public static string FileLabel(string folder, string path) => Path.GetRelativePath(folder, path);

    private static string MoreSuffix(int left) => left > 0 ? $"; +{left} more (see the Lidarr log)" : string.Empty;
}
