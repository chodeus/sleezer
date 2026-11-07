using NzbDrone.Plugin.Sleezer.Core.Utilities;
using NzbDrone.Plugin.Sleezer.Indexers.SubSonic;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.SubSonic;

/// <summary>Names a SubSonic track by the format the server streams it in.</summary>
internal static class SubSonicTrackFormat
{
    // Transcoding changes the format: the server's transcoded type wins, then the format asked for, then the original.
    public static string Extension(SubSonicSearchSong song, PreferredFormatEnum preferred) =>
        Named(song.TranscodedSuffix, song.TranscodedContentType)
        ?? Requested(preferred)
        ?? Named(song.Suffix, song.ContentType)
        ?? ".flac";

    /// <summary>The extension for <paramref name="actual"/> when <paramref name="path"/> names another format; otherwise null.</summary>
    public static string? Corrected(string path, AudioFormat actual)
    {
        string current = Path.GetExtension(path);
        if (actual == AudioFormat.Unknown || AudioFormatHelper.GetAudioCodecFromExtension(current) == actual)
            return null;

        string corrected = AudioFormatHelper.GetFileExtensionForFormat(actual);
        return string.Equals(corrected, current, StringComparison.OrdinalIgnoreCase) ? null : corrected;
    }

    // Subsonic servers stream an AAC transcode as raw ADTS, which is an .aac file, not .m4a.
    private static string? Requested(PreferredFormatEnum preferred) => preferred switch
    {
        PreferredFormatEnum.Mp3 => ".mp3",
        PreferredFormatEnum.Opus => ".opus",
        PreferredFormatEnum.Aac => ".aac",
        PreferredFormatEnum.Flac => ".flac",
        _ => null
    };

    private static string? Named(string? suffix, string? contentType)
    {
        if (!string.IsNullOrWhiteSpace(suffix))
            return "." + suffix.Trim().TrimStart('.').ToLowerInvariant();

        AudioFormat format = AudioFormatHelper.GetAudioFormatFromCodec(contentType?.Split('/')[^1].Trim() ?? string.Empty);
        return format == AudioFormat.Unknown ? null : AudioFormatHelper.GetFileExtensionForFormat(format);
    }
}
