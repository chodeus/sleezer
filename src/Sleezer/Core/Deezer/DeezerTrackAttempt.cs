using System;
using System.Linq;
using NzbDrone.Core.Download.Clients.Deezer.Queue;

namespace NzbDrone.Plugin.Sleezer.Core.Deezer
{
    /// <summary>Decides what one failed Deezer track download means: a rights or region answer is final, anything else is worth another try.</summary>
    public static class DeezerTrackAttempt
    {
        public static bool ShouldRetry(Exception ex, int attempt, int maxAttempts) =>
            attempt < maxAttempts
            && ex is not (InsufficientLicenseRightsException or GeoRestrictionException or TrackUnavailableException)
            && !IsLicenseRightsError(ex)
            && !IsGeoRestrictionError(ex);

        public static bool IsLicenseRightsError(Exception ex)
        {
            for (var cur = ex; cur != null; cur = cur.InnerException)
            {
                var msg = cur.Message ?? string.Empty;
                if (msg.Contains("License token has no sufficient rights", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (cur is AggregateException agg && agg.InnerExceptions.Any(IsLicenseRightsError))
                    return true;
            }
            return false;
        }

        public static bool IsGeoRestrictionError(Exception ex)
        {
            for (var cur = ex; cur != null; cur = cur.InnerException)
            {
                var msg = cur.Message ?? string.Empty;
                if (msg.Contains("not available in your country", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("wrong geolocation", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("geo-restricted", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (cur is AggregateException agg && agg.InnerExceptions.Any(IsGeoRestrictionError))
                    return true;
            }
            return false;
        }
    }
}
