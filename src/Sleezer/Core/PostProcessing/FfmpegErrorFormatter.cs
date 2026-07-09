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

    /// <summary>
    /// ID3v2 tag-parse errors that ffmpeg logs at *error* level but fully recovers
    /// from by skipping the offending tag frame (bad UTF-16 BOM, unreadable
    /// comment / lyrics / text frame). They signal a malformed metadata tag, never
    /// audio-stream corruption. Older ffmpeg (4.x-6.x) prints these at error level;
    /// 8.x no longer does. Sourced from libavformat/id3v2.c.
    /// </summary>
    private static readonly string[] BenignMetadataMarkers =
    {
        "Incorrect BOM value",
        "Cannot read BOM value",
        "Error reading comment frame",
        "Error reading lyrics",
        "Error reading frame",
    };

    /// <summary>
    /// True if an ffmpeg stderr line is an ID3 tag-parse error the demuxer recovered
    /// from by skipping the tag - a metadata defect, not audio corruption.
    /// </summary>
    public static bool IsBenignMetadataNoise(string line)
    {
        foreach (string marker in BenignMetadataMarkers)
            if (line.Contains(marker, System.StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>
    /// Return <paramref name="stderr"/> with benign ID3 tag-parse lines removed. If
    /// only whitespace remains, ffmpeg emitted nothing but metadata noise and the
    /// decode is clean. Genuine decoder errors (Header missing, Invalid data,
    /// backstep, etc.) are preserved so the scanner still fails closed on them.
    /// </summary>
    public static string StripBenignMetadataNoise(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return string.Empty;

        List<string> kept = new();
        foreach (string line in stderr.Split('\n'))
            if (!string.IsNullOrWhiteSpace(line) && !IsBenignMetadataNoise(line))
                kept.Add(line);

        return string.Join("\n", kept).Trim();
    }
}
