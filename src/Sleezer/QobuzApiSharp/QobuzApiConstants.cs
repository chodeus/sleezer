// Vendored from DaveBinM/QobuzApiSharp (GPL-3.0), kept structurally as-is so
// upstream fixes can be pulled by hand. Nullable is off for the same reason.
#nullable disable
namespace QobuzApiSharp
{
    /// <summary>
    /// The qobuz api constants.
    /// </summary>
    internal static class QobuzApiConstants
    {
        /// <summary>
        /// The api base url.
        /// </summary>
        internal const string API_BASE_URL = "https://www.qobuz.com/api.json/0.2";
        /// <summary>
        /// The web player base url.
        /// </summary>
        internal const string WEB_PLAYER_BASE_URL = "https://play.qobuz.com";
        /// <summary>
        /// The Chromecast app_id, used for email/password login.
        /// The production web player app_id is OAuth-only; the Chromecast app_id
        /// still supports direct email/password and token-based authentication.
        /// </summary>
        internal const string CHROMECAST_APP_ID = "425621600";
    }
}