using NzbDrone.Core.Music;
using NzbDrone.Plugin.Sleezer.Core.Model;

namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    /// <summary>Finds the artist's other album under the searched title that a store copy's date points at.</summary>
    public static class SiblingAlbumMatch
    {
        // Versions that came out closer together than this can't be told apart by date.
        private static readonly TimeSpan Margin = TimeSpan.FromDays(14);

        public static Album? DatedSibling(StoreReleaseInfo release, Album target, IEnumerable<Album> artistAlbums, Func<int, List<AlbumRelease>> releasesOf)
        {
            if (AlbumDates.IsUndated(release, DateTime.UtcNow) || YearOnly(release.PublishDate)
                || Gap(release.PublishDate, AlbumDates.Of(target, releasesOf(target.Id))) is not { } toTarget)
                return null;

            // A sibling counts only if verification would have passed the copy for it too.
            string title = release.CandidateTitle ?? release.Album ?? target.Title;
            return artistAlbums
                .Where(a => a.Id != target.Id && StoreReleaseVerifier.TitleMatches(title, a.Title) && !VariantQualifiers.RemixSignaturesConflict(a.Title, title))
                .Select(a => (Album: a, Releases: releasesOf(a.Id)))
                .Where(s => release.TrackCount <= 0 || s.Releases.Any(r => StoreReleaseVerifier.TrackCountCompatible(release.TrackCount, r.TrackCount)))
                .Select(s => (s.Album, Gap: Gap(release.PublishDate, AlbumDates.Of(s.Album, s.Releases))))
                .Where(s => s.Gap + Margin <= toTarget)
                .OrderBy(s => s.Gap)
                .Select(s => s.Album)
                .FirstOrDefault();
        }

        private static TimeSpan? Gap(DateTime published, IEnumerable<DateTime> dates) =>
            dates.Where(d => !YearOnly(d)).Select(d => (TimeSpan?)(published - d).Duration()).Min();

        // MusicBrainz and the stores both record a year-only date as 1 January, too coarse to compare by days.
        private static bool YearOnly(DateTime date) => date is { Month: 1, Day: 1 };
    }
}
