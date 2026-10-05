using System;

namespace NzbDrone.Plugin.Sleezer.Core.Tidal
{
    /// <summary>The saved login and playback client the live Tidal session was loaded from, and when a failed refresh gets another try.</summary>
    public sealed record TidalLoadedLogin(string AccessToken, string PlaybackClient, DateTime RetryRefreshAt)
    {
        // A login saved under a refused client only plays once a refresh lands, so a failed one is retried rather than kept.
        public static readonly TimeSpan RefreshRetryInterval = TimeSpan.FromMinutes(10);

        public static TidalLoadedLogin None { get; } = new(string.Empty, string.Empty, DateTime.MaxValue);

        public static TidalLoadedLogin Loaded(string accessToken, string playbackClient, bool refreshed, DateTime now) =>
            new(accessToken, playbackClient, refreshed ? DateTime.MaxValue : now + RefreshRetryInterval);

        public bool IsCurrentFor(string accessToken, string playbackClient, DateTime now) =>
            string.Equals(AccessToken, accessToken, StringComparison.Ordinal)
            && string.Equals(PlaybackClient, playbackClient, StringComparison.Ordinal)
            && now < RetryRefreshAt;
    }
}
