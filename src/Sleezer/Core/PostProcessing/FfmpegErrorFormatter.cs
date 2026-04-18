using System.Text.RegularExpressions;

namespace NzbDrone.Plugin.Sleezer.Core.PostProcessing;

/// <summary>
/// Pure formatter for ffmpeg stderr output emitted during corruption scans.
/// Strips codec/address prefixes, deduplicates errors, and joins them with pipes
/// so the corrupt-file quarantine log stays readable instead of a 200-line dump.
/// Kept separate from CorruptionScanner so it can be unit-tested without dragging
/// in NLog, TagLib, and Xabe.FFmpeg.
/// </summary>
public static class FfmpegErrorFormatter
{
    private static readonly Regex CodecPrefixPattern = new(@"\[[\w:#/]+ @ 0x[0-9a-fA-F]+\]\s*", RegexOptions.Compiled);
    private static readonly Regex PipeSeparatorPattern = new(@"\s*\|\s*", RegexOptions.Compiled);
    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled);

    public static string CleanFfmpegErrors(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return "Non-zero exit code";

        string cleaned = CodecPrefixPattern.Replace(stderr, string.Empty);
        cleaned = PipeSeparatorPattern.Replace(cleaned, " | ");
        cleaned = WhitespacePattern.Replace(cleaned, " ").Trim();

        string[] parts = cleaned.Split(" | ", System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);
        HashSet<string> seen = new(System.StringComparer.Ordinal);
        List<string> unique = new(parts.Length);
        foreach (string p in parts)
            if (seen.Add(p))
                unique.Add(p);

        return unique.Count > 0 ? string.Join(" | ", unique) : "Non-zero exit code";
    }
}
