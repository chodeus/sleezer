using NzbDrone.Core.Music;
using NzbDrone.Plugin.Sleezer.Core.Model;

namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    /// <summary>The sibling a store copy's date points at, with the two dates that were compared.</summary>
    public readonly record struct SiblingMatch(Album Sibling, DateTime SiblingDate, DateTime TargetDate);

    /// <summary>Finds the artist's other album under the searched title that a store copy's date points at.</summary>
    public static class SiblingAlbumMatch
    {
        // Versions that came out closer together than this can't be told apart by date.
        private static readonly TimeSpan Margin = TimeSpan.FromDays(14);

        public static SiblingMatch? DatedSibling(StoreReleaseInfo release, Album target, IEnumerable<Album> artistAlbums, Func<int, List<AlbumRelease>> releasesOf)
        {
            if (AlbumDates.IsUndated(release, DateTime.UtcNow) || YearOnly(release.PublishDate)
                || Nearest(release.PublishDate, AlbumDates.Of(target, releasesOf(target.Id))) is not { } toTarget)
                return null;

            // A sibling counts only if verification would have passed the copy for it too, and only a
            // judgeable title can name one: TitleMatches passes an empty title, which here would reject.
            string? title = new[] { release.CandidateTitle, release.Album, target.Title }.FirstOrDefault(StoreReleaseVerifier.TitleJudgeable);
            if (title == null)
                return null;

            return artistAlbums
                .Where(a => a.Id != target.Id
                    && StoreReleaseVerifier.TitleJudgeable(a.Title)
                    && StoreReleaseVerifier.TitleMatches(title, a.Title)
                    && !VariantQualifiers.RemixSignaturesConflict(a.Title, title))
                .Select(a => (Album: a, Releases: releasesOf(a.Id)))
                .Where(s => release.TrackCount <= 0 || s.Releases.Any(r => StoreReleaseVerifier.TrackCountCompatible(release.TrackCount, r.TrackCount)))
                .Select(s => (s.Album, Near: Nearest(release.PublishDate, AlbumDates.Of(s.Album, s.Releases))))
                .Where(s => s.Near is { } near && near.Gap + Margin <= toTarget.Gap)
                .OrderBy(s => s.Near!.Value.Gap)
                .Select(s => (SiblingMatch?)new SiblingMatch(s.Album, s.Near!.Value.Date, toTarget.Date))
                .FirstOrDefault();
        }

        private static (TimeSpan Gap, DateTime Date)? Nearest(DateTime published, IEnumerable<DateTime> dates) =>
            dates.Where(d => !YearOnly(d))
                .Select(d => ((TimeSpan Gap, DateTime Date)?)((published - d).Duration(), d))
                .OrderBy(n => n!.Value.Gap)
                .FirstOrDefault();

        // MusicBrainz and the stores both record a year-only date as 1 January, too coarse to compare by days.
        private static bool YearOnly(DateTime date) => date is { Month: 1, Day: 1 };
    }
}
