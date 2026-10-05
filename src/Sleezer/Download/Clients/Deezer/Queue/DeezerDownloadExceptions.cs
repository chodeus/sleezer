using System;

namespace NzbDrone.Core.Download.Clients.Deezer.Queue
{
    public class InsufficientLicenseRightsException : Exception
    {
        public InsufficientLicenseRightsException(string message, Exception? inner = null) : base(message, inner) { }
    }

    public class GeoRestrictionException : Exception
    {
        public GeoRestrictionException(string message, Exception? inner = null) : base(message, inner) { }
    }

    public class TrackUnavailableException : Exception
    {
        public TrackUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
    }
}
