namespace NzbDrone.Plugin.Sleezer.Core.Tidal
{
    /// <summary>Whether Tidal says outright that an album cannot be streamed.</summary>
    public static class TidalAlbumAvailability
    {
        // Only an explicit false counts: an album fetched without these fields is unknown, not unavailable.
        public static bool Unavailable(bool? allowStreaming, bool? streamReady) => allowStreaming == false || streamReady == false;
    }
}
