namespace TidalSharp;

// Some Tidal clients answer a stereo request with Dolby Atmos or 360 audio, which Sleezer doesn't download.
// Only a missing field (an older response) is accepted.
public static class StereoGuard
{
    public static bool IsNotStereo(string? audioMode) =>
        audioMode != null
        && !audioMode.Equals("STEREO", StringComparison.OrdinalIgnoreCase);
}
