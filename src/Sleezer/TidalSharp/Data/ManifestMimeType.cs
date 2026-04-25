namespace TidalSharp.Data;

internal enum ManifestMimeType
{
    MPD,
    BTS,
    VIDEO
}

internal static class ManifestMimeTypeMethods
{
    public static string ToMime(this ManifestMimeType type)
    {
        return type switch
        {
            ManifestMimeType.MPD => "application/dash+xml",
            ManifestMimeType.BTS => "application/vnd.tidal.bts",
            ManifestMimeType.VIDEO => "video/mp2t",
            _ => throw new NotImplementedException(),
        };
    }

    public static ManifestMimeType FromMime(string mime)
    {
        return mime switch
        {
            "application/dash+xml" => ManifestMimeType.MPD,
            "application/vnd.tidal.bts" => ManifestMimeType.BTS,
            "video/mp2t" => ManifestMimeType.VIDEO,
            _ => throw new NotImplementedException(),
        };
    }
}