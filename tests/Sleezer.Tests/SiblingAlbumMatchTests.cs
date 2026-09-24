using System.Globalization;
using NzbDrone.Core.Music;
using NzbDrone.Plugin.Sleezer.Core.Model;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using Xunit;

namespace Sleezer.Tests;

// A guest version often shares the original's title exactly, so only the store copy's date says which one it is.
public class SiblingAlbumMatchTests
{
    private readonly Dictionary<int, List<AlbumRelease>> _releases = [];

    private static DateTime D(string date) => DateTime.Parse(date, CultureInfo.InvariantCulture);

    private Album Album(int id, string title, DateTime? date, int tracks = 1)
    {
        _releases[id] = date is { } d ? [new AlbumRelease { Status = "Official", ReleaseDate = d, TrackCount = tracks }] : [];
        return new Album { Id = id, Title = title, ReleaseDate = date };
    }

    private static StoreReleaseInfo Copy(DateTime published, int tracks = 1) => new() { Title = "copy", PublishDate = published, TrackCount = tracks };

    private Album? Sibling(StoreReleaseInfo copy, Album target, params Album[] others) =>
        SiblingAlbumMatch.DatedSibling(copy, target, [target, .. others], id => _releases[id]);

    [Fact]
    public void A_copy_dated_like_the_original_is_not_the_guest_version()
    {
        Album original = Album(1, "Stateside", D("2025-04-25"));
        Album guest = Album(2, "Stateside", D("2025-10-10"));

        Assert.Same(original, Sibling(Copy(D("2025-04-24")), guest, original));
        Assert.Null(Sibling(Copy(D("2025-10-10")), guest, original));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(13, false)]
    [InlineData(14, true)]
    public void Versions_out_within_two_weeks_of_each_other_are_left_alone(int daysApart, bool rejected)
    {
        Album searched = Album(1, "Family", D("2025-06-15"));
        Album other = Album(2, "Family", D("2025-06-15").AddDays(-daysApart));

        Assert.Equal(rejected, Sibling(Copy(D("2025-06-15").AddDays(-daysApart)), searched, other) != null);
    }

    [Fact]
    public void A_reissue_of_the_searched_album_vouches_for_its_date()
    {
        Album original = Album(1, "Stateside", D("2025-04-25"));
        Album guest = Album(2, "Stateside", D("2025-10-10"));
        _releases[2].Add(new AlbumRelease { Status = "Official", ReleaseDate = D("2025-04-25"), TrackCount = 1 });

        Assert.Null(Sibling(Copy(D("2025-04-24")), guest, original));
    }

    [Fact]
    public void An_album_under_another_title_is_not_a_sibling()
    {
        Album other = Album(1, "Crossroads", D("2025-04-25"));

        Assert.Null(Sibling(Copy(D("2025-04-24")), Album(2, "Stateside", D("2025-10-10")), other));
    }

    // The remix copy passes as the remix single, never as the original it shares a title with.
    [Fact]
    public void A_copy_whose_variant_the_sibling_lacks_is_not_the_sibling()
    {
        Album original = Album(1, "Leave a Trace", D("2015-08-21"));
        Album remix = Album(2, "Leave a Trace (Four Tet remix)", D("2015-09-11"));
        StoreReleaseInfo copy = Copy(D("2015-08-21"));
        copy.CandidateTitle = "Leave a Trace (Four Tet Remix)";

        Assert.Null(Sibling(copy, remix, original));
    }

    [Fact]
    public void A_sibling_whose_track_count_cannot_fit_the_copy_is_ignored()
    {
        Album fullLength = Album(1, "Stateside", D("2025-04-25"), tracks: 12);

        Assert.Null(Sibling(Copy(D("2025-04-24")), Album(2, "Stateside", D("2025-10-10")), fullLength));
    }

    // Parsers stamp UtcNow when the store gave no date; that says nothing about a sibling out this week.
    [Fact]
    public void An_undated_copy_is_left_alone()
    {
        Album recent = Album(1, "Stateside", DateTime.UtcNow.Date);

        Assert.Null(Sibling(Copy(DateTime.UtcNow), Album(2, "Stateside", D("2020-01-10")), recent));
    }

    [Theory]
    [InlineData("2011-01-01", "2011-01-01")]
    [InlineData("2011-01-02", "2011-01-01")]
    [InlineData("2011-01-01", "2011-01-02")]
    public void A_year_only_date_on_either_side_is_left_alone(string published, string siblingDate)
    {
        Album other = Album(1, "Circles", D(siblingDate));

        Assert.Null(Sibling(Copy(D(published)), Album(2, "Circles", D("2011-03-11")), other));
    }

    [Fact]
    public void A_searched_album_with_no_date_is_left_alone()
    {
        Album original = Album(1, "Stateside", D("2025-04-25"));

        Assert.Null(Sibling(Copy(D("2025-04-24")), Album(2, "Stateside", null), original));
    }
}
