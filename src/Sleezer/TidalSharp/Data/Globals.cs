namespace TidalSharp.Data;

internal static class Globals
{
    public const string API_OAUTH2_TOKEN = "https://auth.tidal.com/v1/oauth2/token";
    public const string API_OAUTH2_DEVICE_AUTH = "https://auth.tidal.com/v1/oauth2/device_authorization";
    public const string API_PKCE_AUTH = "https://login.tidal.com/authorize";
    public const string API_V1_LOCATION = "https://api.tidal.com/v1/";
    public const string API_V2_LOCATION = "https://api.tidal.com/v2/";
    public const string OPENAPI_V2_LOCATION = "https://openapi.tidal.com/v2/";

    public const string PKCE_URI_REDIRECT = "https://tidal.com/android/login/auth";

    // Credentials extracted from Tidal's own clients (Android, Android TV).
    // Same provenance as TrevTV/Lidarr.Plugin.Tidal and oskvr37/tiddl. Treat as
    // semi-public; Tidal can rotate these and break the plugin at any time.
    public const string CLIENT_ID = "zU4XHVVkc2tDPo4t";
    public const string CLIENT_SECRET = "VJKhDFqJPqvsPVNBV6ukXTJmwlvbttP7wlMlrc72se4=";

    public const string CLIENT_ID_PKCE = "6BDSRdpK9hqEBTgU";
    public const string CLIENT_SECRET_PKCE = "xeuPmY7nbpZ9IIbLAcQ93shka1VNheUAqN6IcszjTG8=";

    // Device-authorization client (TV-class). Used by Session.StartDeviceAuthorization.
    public const string CLIENT_ID_DEVICE = "fX2JxdmntZWK0ixT";
    public const string CLIENT_SECRET_DEVICE = "1Nn9AfDAjxrgJFJbKNWLeAyKGVGmINuXPPLHVXAvxAg=";

    public static string GetImageUrl(string hash, MediaResolution res)
        => string.Format(IMAGE_URL_TEMPLATE, hash.Replace('-', '/'), (int)res);

    public static string GetImageResoursePath(string hash, MediaResolution res)
        => string.Format(IMAGE_URL_RESOURCE_TEMPLATE, hash.Replace('-', '/'), (int)res);

    public static string GetVideoUrl(string hash, MediaResolution res)
        => string.Format(IMAGE_URL_TEMPLATE, hash.Replace('-', '/'), (int)res);

    public const string IMAGE_URL_BASE = "https://resources.tidal.com/";
    private const string IMAGE_URL_RESOURCE_TEMPLATE = "/images/{0}/{1}x{1}.jpg";
    private const string IMAGE_URL_TEMPLATE = "https://resources.tidal.com/images/{0}/{1}x{1}.jpg";
    private const string VIDEO_URL_TEMPLATE = "https://resources.tidal.com/videos/{0}/{1}x{1}.mp4";
}
