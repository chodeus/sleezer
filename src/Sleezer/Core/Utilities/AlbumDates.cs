using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    /// <summary>The dates an album came out on, and whether a search result carries a real date at all.</summary>
    public static class AlbumDates
    {
        // Parsers stamp UtcNow when they have no usable date, so a stamp this fresh is that
        // sentinel, not a real release date.
        private static readonly TimeSpan JustStamped = TimeSpan.FromHours(1);

        // MusicBrainz files a remaster or reissue as a release of the album, so each official
        // release date is a date this album came out on.
        public static IEnumerable<DateTime> Of(Album album, IEnumerable<AlbumRelease> releases) =>
            releases.Where(r => r.Status == ReleaseStatus.Official.Name)
                .Select(r => r.ReleaseDate)
                .Append(album.ReleaseDate)
                .OfType<DateTime>();

        public static bool IsUndated(ReleaseInfo release, DateTime nowUtc)
        {
            if (release.PublishDate == default)
                return true;

            // A future date is a scheduled release, never the sentinel.
            TimeSpan age = nowUtc - release.PublishDate.ToUniversalTime();
            return age >= TimeSpan.Zero && age < JustStamped;
        }
    }
}
