using System.Collections.Generic;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Plugin.Sleezer.Core.Model
{
    /// <summary>A store search hit carrying the album facts StoreReleaseVerifier checks against MusicBrainz.</summary>
    public class StoreReleaseInfo : ReleaseInfo, IVerifiableRelease
    {
        public string? ArtistId { get; set; }

        // Title plus the store's version qualifier — Deezer keeps "(Extended Mix)" in a separate field.
        public string? CandidateTitle { get; set; }

        public int TrackCount { get; set; }
        public int TotalDurationSeconds { get; set; }
        public IReadOnlyList<int>? TrackDurationsSeconds { get; set; }

        public string? Rejection { get; set; }
    }
}
