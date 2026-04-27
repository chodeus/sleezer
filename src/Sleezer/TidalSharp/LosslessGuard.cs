using System;
using TidalSharp.Data;

namespace TidalSharp;

// Decides whether a Tidal playbackinfopostpaywall response represents a silent
// codec downgrade (LOSSLESS request → mp4a delivery). Tidal does this without
// any error response when per-track licensing in the user's region forbids
// lossless playback. Accepting the downgrade silently lets AAC files land in a
// Lossless quality bucket — the user thinks they have FLAC when they don't.
//
// Pure helper, mirrors ExpiredTokenDetector — keeps the rules in one place so
// the Downloader and any future caller (e.g. an integration test harness) can
// share the same definition of "this download should fail".
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
