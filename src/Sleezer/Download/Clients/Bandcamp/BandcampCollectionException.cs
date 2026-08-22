using System;

namespace NzbDrone.Core.Download.Clients.Bandcamp
{
    /// <summary>
    /// Raised when Bandcamp's collection response cannot be read. Distinct from an
    /// empty collection: an expired session, an HTML error page and a rate-limit body
    /// all parse to nothing, and callers must not treat that as "owns no music".
    /// </summary>
    public class BandcampCollectionException : Exception
    {
        public BandcampCollectionException(string message)
            : base(message)
        {
        }

        public BandcampCollectionException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
