namespace TidalSharp;

// Tidal answers a FULL request with a 30-second PREVIEW when the account has no active
// subscription for the track, without any error. Missing means an older response: accept it.
public static class PreviewGuard
{
    public static bool IsPreview(string? assetPresentation) =>
        !string.IsNullOrEmpty(assetPresentation)
        && !assetPresentation.Equals("FULL", StringComparison.OrdinalIgnoreCase);
}
