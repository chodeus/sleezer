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
        if (lossySource)
            return lossyTarget ? Upsampled(sourceBitrate, targetBitrate, "kbps", "bitrate") : "a lossy file can't become lossless";

        return lossyTarget ? null : Upsampled(sourceBitDepth, targetBitDepth, "-bit", "bit depth");
    }

    // A source that can't be read could be upsampled, so it is left as it is.
    private static string? Upsampled(int? source, int? target, string unit, string measure)
    {
        if (target is not int wanted || (source > 0 && wanted <= source))
            return null;

        return source > 0 ? $"{source}{unit} is below the {wanted}{unit} target" : $"its {measure} could not be read";
    }
}
