using System;
using TidalSharp.Data;

namespace TidalSharp;

// Tidal answers a lossless request with mp4a and no error for a track not licensed lossless, or for every track
// when it caps the token's client (#173); accepting that would put AAC in a Lossless quality bucket.
public static class LosslessGuard
{
    public static bool IsLosslessTier(AudioQuality q) =>
        q == AudioQuality.LOSSLESS || q == AudioQuality.HI_RES_LOSSLESS;

    public static bool CodecIsLossless(string? codec) =>
        !string.IsNullOrEmpty(codec) && codec.Contains("flac", StringComparison.OrdinalIgnoreCase);

    public static bool ShouldRejectAsSilentDowngrade(AudioQuality requested, string? deliveredCodec) =>
        IsLosslessTier(requested)
        && !string.IsNullOrEmpty(deliveredCodec)
        && !CodecIsLossless(deliveredCodec);
}
