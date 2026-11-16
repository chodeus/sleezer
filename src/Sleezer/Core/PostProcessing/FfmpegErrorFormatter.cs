using System.Text.RegularExpressions;

namespace NzbDrone.Plugin.Sleezer.Core.PostProcessing;

/// <summary>
/// Pure formatter for ffmpeg stderr output emitted during corruption scans.
/// Strips codec/address prefixes and ffmpeg's follow-on lines, deduplicates errors, and joins them with pipes
/// so the corrupt-file quarantine log stays readable instead of a 200-line dump.
/// Kept separate from CorruptionScanner so it can be unit-tested without dragging
/// in NLog, TagLib, and Xabe.FFmpeg.
/// </summary>
public static class FfmpegErrorFormatter
{
    private static readonly Regex CodecPrefixPattern = new(@"\[[\w:#/]+ @ 0x[0-9a-fA-F]+\]\s*", RegexOptions.Compiled);
    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled);

    private const int LogExcerptLines = 20;

    // The muxer's complaint about packets a bad frame left behind; never the cause.
    private const string MuxerDtsMarker = "non monotonically increasing dts";

    // Lines ffmpeg prints after the decoder's own error (n9.0.2; "Error while decoding stream" is 4.x-6.x).
    private static readonly string[] FollowOnMarkers =
    {
        "decode_frame() failed",
        "Decoding error:",
        "Error submitting packet to decoder:",
        "Error processing packet in decoder:",
        "Task finished with error code:",
        "Terminating thread with return code",
        "Error while decoding stream",
        MuxerDtsMarker,
    };

    /// <summary>The decoder's own errors from ffmpeg stderr, deduplicated and joined with " | "; one follow-on line if that is all there is.</summary>
    public static string CleanFfmpegErrors(string stderr)
    {
        List<string> errors = DistinctErrors(stderr);
        if (errors.Count == 0)
            return "Non-zero exit code";

        List<string> specific = errors.FindAll(e => !IsFollowOn(e));
        if (specific.Count > 0)
            return string.Join(" | ", specific);

        return errors.Find(e => !e.Contains(MuxerDtsMarker, System.StringComparison.Ordinal)) ?? errors[0];
    }

    /// <summary>The corruption reason for a file that failed ffmpeg's decode check.</summary>
    public static string DecodeFailureReason(string stderr, int exitCode)
    {
        string significant = StripBenignMetadataNoise(stderr);
        if (significant.Length > 0)
            return $"Audio won't decode ({CleanFfmpegErrors(significant)})";

        return string.IsNullOrWhiteSpace(stderr)
            ? $"ffmpeg failed with exit code {exitCode}"
            : $"ffmpeg stopped at a damaged tag or cover art ({CleanFfmpegErrors(stderr)})";
    }

    /// <summary>The first lines of ffmpeg stderr for the Debug log, with a count of the rest.</summary>
    public static string LogExcerpt(string stderr)
    {
        string[] lines = stderr.Trim().Split('\n');
        if (lines.Length <= LogExcerptLines)
            return string.Join('\n', lines);

        return string.Join('\n', lines.Take(LogExcerptLines)) + $"\n({lines.Length - LogExcerptLines} more lines)";
    }

    private static List<string> DistinctErrors(string stderr)
    {
        List<string> unique = new();
        if (string.IsNullOrWhiteSpace(stderr))
            return unique;

        HashSet<string> seen = new(System.StringComparer.Ordinal);
        foreach (string line in CodecPrefixPattern.Replace(stderr, string.Empty).Split('\n'))
        {
            string error = WhitespacePattern.Replace(line, " ").Trim();
            if (error.Length > 0 && seen.Add(error))
                unique.Add(error);
        }

        return unique;
    }

    private static bool IsFollowOn(string error) => ContainsAny(error, FollowOnMarkers);

    private static bool ContainsAny(string line, string[] markers)
    {
        foreach (string marker in markers)
            if (line.Contains(marker, System.StringComparison.Ordinal))
                return true;
        return false;
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
        // Cover-art block, not audio (libavformat/flac_picture.c). Without
        // AV_EF_EXPLODE the demuxer skips the picture and decodes normally.
        "Could not read mimetype from an attached picture",
        "Error parsing attached picture",
    };

    /// <summary>
    /// True when ffmpeg failed while parsing an embedded cover-art block. Under
    /// `-err_detect explode` this aborts INPUT OPEN, before `-map 0:a` can limit
    /// the scan to audio — so the audio is never actually judged. The caller must
    /// re-verify without explode rather than trust this verdict.
    /// </summary>
    public static bool IsAttachedPictureFailure(string stderr) =>
        !string.IsNullOrWhiteSpace(stderr) &&
        (stderr.Contains("Could not read mimetype from an attached picture", System.StringComparison.Ordinal) ||
         stderr.Contains("Error parsing attached picture", System.StringComparison.Ordinal));

    /// <summary>
    /// True if an ffmpeg stderr line is an ID3 tag-parse error the demuxer recovered
    /// from by skipping the tag - a metadata defect, not audio corruption.
    /// </summary>
    public static bool IsBenignMetadataNoise(string line) => ContainsAny(line, BenignMetadataMarkers);

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
