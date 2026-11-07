using NzbDrone.Plugin.Sleezer.Core.Utilities;

namespace NzbDrone.Plugin.Sleezer.Metadata.FFmpeg;

/// <summary>Keeps a conversion from faking quality, for rules and Convert toggles alike.</summary>
public static class ConversionGuard
{
    /// <summary>Why converting a <paramref name="source"/> file to <paramref name="target"/> would fake quality, or null when it would not.</summary>
    public static string? BlockReason(AudioFormat source, int? sourceBitrate, int? sourceBitDepth, AudioFormat target, int? targetBitrate, int? targetBitDepth)
    {
        bool lossySource = AudioFormatHelper.IsLossyFormat(source);
        bool lossyTarget = AudioFormatHelper.IsLossyFormat(target);
        if (lossySource && !lossyTarget)
            return "a lossy file can't become lossless";

        // A source that can't be read could be upsampled, so it is left as it is.
        if (lossySource && targetBitrate is int bitrate && !(sourceBitrate > 0 && bitrate <= sourceBitrate))
            return sourceBitrate > 0 ? $"{sourceBitrate}kbps is below the {bitrate}kbps target" : "its bitrate could not be read";

        if (!lossySource && !lossyTarget && targetBitDepth is int bitDepth && !(sourceBitDepth > 0 && bitDepth <= sourceBitDepth))
            return sourceBitDepth > 0 ? $"{sourceBitDepth}-bit is below the {bitDepth}-bit target" : "its bit depth could not be read";

        return null;
    }
}
