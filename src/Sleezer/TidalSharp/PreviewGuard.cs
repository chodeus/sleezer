namespace TidalSharp;

// Tidal answers a FULL request with a 30-second PREVIEW when the account has no active
// subscription for the track, without any error. Only a missing field (an older response) is accepted.
public static class PreviewGuard
{
    public static bool IsPreview(string? assetPresentation) =>
        assetPresentation != null
        && !assetPresentation.Equals("FULL", StringComparison.OrdinalIgnoreCase);
}
