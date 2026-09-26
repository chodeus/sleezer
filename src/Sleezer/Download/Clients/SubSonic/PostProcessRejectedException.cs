using System;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.SubSonic
{
    /// Thrown when the corruption scan condemns a SubSonic download. Surfacing it as a
    /// fault is what marks the request Failed, so Lidarr re-searches instead of importing
    /// files the scan just rejected.
    public class PostProcessRejectedException : Exception
    {
        public PostProcessRejectedException(string message) : base(message) { }
    }
}
