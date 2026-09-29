using NzbDrone.Core.Music;
using NzbDrone.Plugin.Sleezer.Core.Model;

namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    /// <summary>The sibling a store copy's date points at, with the two dates that were compared.</summary>
    public readonly record struct SiblingMatch(Album Sibling, DateTime SiblingDate, DateTime TargetDate);

    /// <summary>Finds the artist's other album a store copy belongs to: the one its title names, its date points at, or its guests match under the searched title.</summary>
    public static class SiblingAlbumMatch
    {
        // Versions that came out closer together than this can't be told apart by date.
        private static readonly TimeSpan Margin = TimeSpan.FromDays(14);

        /// <summary>The artist's other album whose full title, qualifiers included, the store copy carries while the searched one's differs.</summary>
        // Stores date a re-recording like its original, so the title is the only tell DatedSibling can't see.
        public static Album? NamedSibling(StoreReleaseInfo release, Album target, IEnumerable<Album> artistAlbums)
        {
            string named = StoreReleaseVerifier.Normalize(OwnTitle(release));
            if (named.Length == 0 || named == StoreReleaseVerifier.Normalize(target.Title))
                return null;

            return artistAlbums.FirstOrDefault(a => a.Id != target.Id && StoreReleaseVerifier.Normalize(a.Title) == named);
        }

        public static SiblingMatch? DatedSibling(StoreReleaseInfo release, Album target, IEnumerable<Album> artistAlbums, Func<int, List<AlbumRelease>> releasesOf)
        {
            if (AlbumDates.IsUndated(release, DateTime.UtcNow) || YearOnly(release.PublishDate)
                || Nearest(release.PublishDate, AlbumDates.Of(target, releasesOf(target.Id))) is not { } toTarget)
                return null;

            // Only a judgeable title can name a sibling: TitleMatches passes an empty one, which here would reject.
            string? title = OwnTitle(release) ?? (StoreReleaseVerifier.TitleJudgeable(target.Title) ? target.Title : null);
            if (title == null)
                return null;

            // A sibling counts only if verification would pass the copy for it too. The title test
            // first is a cheap pre-filter so releases are fetched only for same-titled albums.
            return artistAlbums
                .Where(a => a.Id != target.Id && StoreReleaseVerifier.TitleJudgeable(a.Title) && StoreReleaseVerifier.TitleMatches(title, a.Title))
                .Select(a => (Album: a, Releases: releasesOf(a.Id)))
                .Where(s => StoreReleaseVerifier.PassesFor(release, title, s.Album, s.Releases))
                .Select(s => (s.Album, Near: Nearest(release.PublishDate, AlbumDates.Of(s.Album, s.Releases))))
                .Where(s => s.Near is { } near && near.Gap + Margin <= toTarget.Gap)
                .OrderBy(s => s.Near!.Value.Gap)
                .Select(s => (SiblingMatch?)new SiblingMatch(s.Album, s.Near!.Value.Date, toTarget.Date))
                .FirstOrDefault();
        }

        /// <summary>The artist's same-titled album whose credited guests the store copy names exactly, while the searched one's differ.</summary>
        // Lidarr keeps one artist per album, so versions that differ only by guest look alike to the checks above.
        public static Album? GuestSibling(StoreReleaseInfo release, Album target, IEnumerable<Album> artistAlbums, string searchedArtist, Func<Album, IReadOnlyList<string>?> guestsOf)
        {
            string title = StoreReleaseVerifier.Normalize(target.Title);
            List<Album> siblings = [.. artistAlbums.Where(a => a.Id != target.Id && StoreReleaseVerifier.Normalize(a.Title) == title)];
            if (siblings.Count == 0 || guestsOf(target) is not { } wanted)
                return null;

            string named = GuestCredits.OfStoreCopy([release.CandidateTitle, release.Album], release.MainArtists, searchedArtist);
            if (GuestCredits.NamesExactly(named, wanted, searchedArtist))
                return null;

            return siblings.FirstOrDefault(s => guestsOf(s) is { } guests && GuestCredits.NamesExactly(named, guests, searchedArtist));
        }

        private static (TimeSpan Gap, DateTime Date)? Nearest(DateTime published, IEnumerable<DateTime> dates) =>
            dates.Where(d => !YearOnly(d))
                .Select(d => ((TimeSpan Gap, DateTime Date)?)((published - d).Duration(), d))
                .OrderBy(n => n!.Value.Gap)
                .FirstOrDefault();

        // The store's own title for the copy: one it can't judge falls through to the album name.
        private static string? OwnTitle(StoreReleaseInfo release) =>
            new[] { release.CandidateTitle, release.Album }.FirstOrDefault(StoreReleaseVerifier.TitleJudgeable);

        // MusicBrainz and the stores both record a year-only date as 1 January, too coarse to compare by days.
        private static bool YearOnly(DateTime date) => date is { Month: 1, Day: 1 };
    }
}
