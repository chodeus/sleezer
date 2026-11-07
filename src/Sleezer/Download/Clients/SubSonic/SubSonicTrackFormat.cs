using NzbDrone.Plugin.Sleezer.Core.Utilities;
using NzbDrone.Plugin.Sleezer.Indexers.SubSonic;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.SubSonic;

/// <summary>Names a SubSonic track by the format the server streams it in.</summary>
internal static class SubSonicTrackFormat
{
    // A format asked for is what the server streams; with none (Raw), its default transcode, then the original.
    public static string Extension(SubSonicSearchSong song, PreferredFormatEnum preferred) =>
        Requested(preferred)
        ?? Named(song.TranscodedSuffix, song.TranscodedContentType)
        ?? Named(song.Suffix, song.ContentType)
        ?? ".flac";

    /// <summary>The extension for <paramref name="actual"/> when <paramref name="path"/> names another format; otherwise null.</summary>
    public static string? Corrected(string path, AudioFormat actual)
    {
        string current = Path.GetExtension(path);
        if (actual == AudioFormat.Unknown || AudioFormatHelper.GetAudioCodecFromExtension(current) == actual ||
            (Containers.TryGetValue(current, out AudioFormat[]? holds) && holds.Contains(actual)))
            return null;

        string corrected = AudioFormatHelper.GetFileExtensionForFormat(actual);
        return string.Equals(corrected, current, StringComparison.OrdinalIgnoreCase) ? null : corrected;
    }

    // Containers that hold more than one codec keep their name: AIFF holds PCM, Ogg holds FLAC and Opus, MP4 holds ALAC.
    private static readonly Dictionary<string, AudioFormat[]> Containers = new(StringComparer.OrdinalIgnoreCase)
    {
        [".aiff"] = [AudioFormat.WAV], [".aif"] = [AudioFormat.WAV], [".aifc"] = [AudioFormat.WAV],
        [".ogg"] = [AudioFormat.FLAC, AudioFormat.Opus], [".oga"] = [AudioFormat.FLAC, AudioFormat.Opus, AudioFormat.Vorbis],
        [".m4a"] = [AudioFormat.ALAC], [".mp4"] = [AudioFormat.ALAC],
    };

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

        AudioFormat format = IndexerParserHelper.DetermineFormat(null, contentType, AudioFormat.Unknown);
        return format == AudioFormat.Unknown ? null : AudioFormatHelper.GetFileExtensionForFormat(format);
    }
}
