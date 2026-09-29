using NzbDrone.Core.Music;
using NzbDrone.Plugin.Sleezer.Core.Model;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using Xunit;

namespace Sleezer.Tests;

// Versions of a single that differ only by guest share title, date and tracklist; only the credit tells them apart.
public class GuestSiblingTests
{
    private const string Main = "Main Artist";

    private readonly Dictionary<int, IReadOnlyList<string>?> _credits = [];
    private readonly List<int> _looked = [];

    private Album Album(int id, string title, params string[] guests)
    {
        _credits[id] = guests;
        return new Album { Id = id, Title = title };
    }

    private static StoreReleaseInfo Copy(string title, params string[] mainArtists) => new() { Title = "copy", CandidateTitle = title, Album = title, MainArtists = mainArtists };

    private Album? Sibling(StoreReleaseInfo copy, Album target, params Album[] others) =>
        SiblingAlbumMatch.GuestSibling(copy, target, [target, .. others], Main, a => { _looked.Add(a.Id); return _credits[a.Id]; });

    [Fact]
    public void A_copy_naming_the_other_versions_guests_points_at_it()
    {
        Album original = Album(1, "Song", "Guest A");
        Album version = Album(2, "Song", "Guest A", "Guest B");

        Assert.Same(original, Sibling(Copy("Song (feat. Guest A)"), version, original));
    }

    [Fact]
    public void A_copy_naming_the_searched_versions_guests_is_kept()
    {
        Album original = Album(1, "Song", "Guest A");
        Album version = Album(2, "Song", "Guest A", "Guest B");

        Assert.Null(Sibling(Copy("Song (feat. Guest A & Guest B)"), version, original));
    }

    // A duplicate release group credits the same guests; the credit can't choose, so it must not reject.
    [Fact]
    public void A_sibling_with_the_same_guests_cannot_claim_the_copy()
    {
        Album duplicate = Album(1, "Song", "Guest A");
        Album target = Album(2, "Song", "Guest A");

        Assert.Null(Sibling(Copy("Song (feat. Guest A)"), target, duplicate));
    }

    [Fact]
    public void A_copy_matching_neither_credit_is_kept()
    {
        Album original = Album(1, "Song", "Guest A");
        Album version = Album(2, "Song", "Guest A", "Guest B");

        Assert.Null(Sibling(Copy("Song (feat. Someone Else)"), version, original));
    }

    [Fact]
    public void An_unreadable_credit_for_the_searched_album_keeps_the_copy()
    {
        Album original = Album(1, "Song", "Guest A");
        Album version = Album(2, "Song", "Guest A", "Guest B");
        _credits[2] = null;

        Assert.Null(Sibling(Copy("Song (feat. Guest A)"), version, original));
    }

    [Fact]
    public void An_unreadable_sibling_credit_cannot_claim_the_copy()
    {
        Album original = Album(1, "Song", "Guest A");
        Album version = Album(2, "Song", "Guest A", "Guest B");
        _credits[1] = null;

        Assert.Null(Sibling(Copy("Song (feat. Guest A)"), version, original));
    }

    [Fact]
    public void Without_a_same_titled_sibling_musicbrainz_is_never_asked()
    {
        Album version = Album(2, "Song", "Guest A", "Guest B");
        Album other = Album(3, "Other Song", "Guest A");

        Assert.Null(Sibling(Copy("Song (feat. Guest A)"), version, other));
        Assert.Empty(_looked);
    }

    [Fact]
    public void A_second_main_artist_counts_as_a_guest()
    {
        Album original = Album(1, "Song", "Guest A");
        Album version = Album(2, "Song", "Guest A", "Guest B");

        Assert.Null(Sibling(Copy("Song (feat. Guest A)", Main, "Guest B"), version, original));
    }

    [Theory]
    [InlineData("Song (feat. A, B & C)", new[] { "a", "b", "c" })]
    [InlineData("Song [featuring A and B]", new[] { "a", "b" })]
    [InlineData("Song (with A)", new[] { "a" })]
    [InlineData("Song (A remix)", new string[0])]
    [InlineData("Song (feat. Main Artist & A)", new[] { "a" })]
    public void Store_guests_come_from_the_featured_brackets(string title, string[] expected)
    {
        Assert.Equal(expected.ToHashSet(), GuestCredits.OfStoreCopy(title, [], Main));
    }

    [Fact]
    public void MusicBrainz_credits_leave_out_the_primary_artist_and_keep_the_credited_name()
    {
        const string json = """
            {"artist-credit":[
              {"name":"Main Artist","joinphrase":", ","artist":{"id":"primary-id","name":"Main Artist"}},
              {"name":"Guest Credited","joinphrase":" & ","artist":{"id":"guest-id","name":"Guest Canonical"}},
              {"name":"Guest B","joinphrase":"","artist":{"id":"guest-b-id","name":"Guest B"}}]}
            """;

        Assert.Equal(["Guest Credited", "Guest B"], GuestCredits.FromMusicBrainz(json, "primary-id"));
    }
}
