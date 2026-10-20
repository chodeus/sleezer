using NzbDrone.Plugin.Sleezer.Core.PostProcessing;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using Xunit;

namespace Sleezer.Tests;

public class ArtistCreditTests
{
    private static readonly ArtistCredit Moonward = new("Moonward", ["Lunar Drift"]);

    [Theory]
    [InlineData("MOONWARD")]
    [InlineData("Lunar Drift")]
    [InlineData("Salt Road;Moonward")]
    [InlineData("Salt Road & Moonward")]
    [InlineData("Salt Road x Moonward")]
    [InlineData("Salt Road, Moonward, Glass Harbour")]
    [InlineData("Moonward presents Glass Harbour")]
    [InlineData("Moon Ward")]
    public void A_credit_naming_the_artist_names_them(string credit) =>
        Assert.True(Moonward.IsNamedBy(credit));

    // A short name scores under the fuzzy floor once its word break is gone.
    [Fact]
    public void A_name_written_without_its_word_break_names_the_artist() =>
        Assert.True(new ArtistCredit("Ra Vo").IsNamedBy("RaVo"));

    [Theory]
    [InlineData("MOONWARD")]
    [InlineData("Lunar Drift")]
    [InlineData("Salt Road;Moonward")]
    [InlineData("Salt Road / Moonward")]
    public void A_credit_listing_the_artist_lists_them(string credit) =>
        Assert.True(Moonward.IsListedIn(credit));

    // Listed only as a whole part: a longer act or a duo's own name is someone else.
    [Theory]
    [InlineData("Moonward Falls")]
    [InlineData("Salt Road & Moonward")]
    [InlineData("Salt Road, Moonward")]
    [InlineData("Moonward presents Glass Harbour")]
    public void A_credit_that_only_contains_the_artist_does_not_list_them(string credit) =>
        Assert.False(Moonward.IsListedIn(credit));

    [Fact]
    public void Words_scattered_across_a_collaboration_do_not_list_the_artist() =>
        Assert.False(new ArtistCredit("Ivy Moss").IsListedIn("Ivy Lane & Peter Moss"));

    [Theory]
    [InlineData("Glass/Harbour", "Glass/Harbour;Salt Road")]
    [InlineData("Salt & Road", "Moonward;Salt & Road")]
    public void A_name_holding_a_separator_is_still_listed(string artist, string credit) =>
        Assert.True(new ArtistCredit(artist).IsListedIn(credit));

    [Theory]
    [InlineData("Salt Road")]
    [InlineData("Various Artists")]
    [InlineData("")]
    [InlineData(null)]
    public void A_credit_without_the_artist_does_not_name_them(string? credit) =>
        Assert.False(Moonward.IsNamedBy(credit));

    // A shared first word or a similar sound is another act.
    [Theory]
    [InlineData("Moonward Lake", "Moonward Hills")]
    [InlineData("Glasstone", "Brasstone")]
    public void A_near_miss_name_does_not_name_the_artist(string artist, string credit) =>
        Assert.False(new ArtistCredit(artist).IsNamedBy(credit));

    [Fact]
    public void A_dotted_name_inside_a_comma_list_names_the_artist() =>
        Assert.True(new ArtistCredit("D.X.R").IsNamedBy("Lantern Kid, D.X.R, Salt Road"));

    [Fact]
    public void Only_a_various_artists_credit_names_various_artists()
    {
        ArtistCredit various = new("Various Artists");

        Assert.True(various.IsNamedBy("V/A"));
        Assert.False(various.IsNamedBy("Moonward"));
    }

    // DJ-pool files carry a site name as the album artist and the real artist as the performer.
    [Fact]
    public void A_watermark_album_artist_beside_the_artist_is_named() =>
        Assert.Equal(CreditVerdict.Named, FileCredits.Judge(["https://glassharbour.example", "Moonward"], Moonward));

    [Fact]
    public void Credits_naming_only_others_are_foreign() =>
        Assert.Equal(CreditVerdict.Foreign, FileCredits.Judge(["Salt Road", "Salt Road & Glass Harbour"], Moonward));

    // A compilation tags Various Artists as the album artist; the performer decides.
    [Theory]
    [InlineData("Moonward", CreditVerdict.Named)]
    [InlineData("Salt Road", CreditVerdict.Foreign)]
    public void A_various_artists_album_artist_leaves_the_performer_to_decide(string performer, CreditVerdict expected) =>
        Assert.Equal(expected, FileCredits.Judge(["Various Artists", performer], Moonward));

    [Fact]
    public void No_readable_credit_is_unjudged()
    {
        Assert.Equal(CreditVerdict.Unjudged, FileCredits.Judge(["Various Artists", "", null], Moonward));
        Assert.Equal(CreditVerdict.Unjudged, FileCredits.Judge([], Moonward));
    }

    // A featured guest is not the file's artist, bracketed or not.
    [Theory]
    [InlineData("Salt Road (feat. Moonward)")]
    [InlineData("Salt Road feat. Moonward")]
    [InlineData("Salt Road ft. Moonward")]
    [InlineData("Salt Road featuring Moonward")]
    public void A_featured_guest_does_not_name_the_artist(string credit) =>
        Assert.Equal(CreditVerdict.Foreign, FileCredits.Judge([credit], Moonward));

    [Fact]
    public void The_artist_before_a_bare_guest_is_named() =>
        Assert.Equal(CreditVerdict.Named, FileCredits.Judge(["Lunar Drift feat. Salt Road"], Moonward));

    // "With" inside a name is no guest credit.
    [Fact]
    public void A_name_holding_with_is_named() =>
        Assert.Equal(CreditVerdict.Named, FileCredits.Judge(["Ride With Lanterns"], new ArtistCredit("Ride With Lanterns")));

    [Fact]
    public void A_collaborator_listed_first_leaves_the_artist_named() =>
        Assert.Equal(CreditVerdict.Named, FileCredits.Judge(["Salt Road;Moonward"], Moonward));

    // Loosely named but not listed: neither given the artist's name nor left out of the title fallback.
    [Theory]
    [InlineData("Salt Road & Moonward")]
    [InlineData("Moonward Falls")]
    public void A_credit_only_containing_the_artist_is_unjudged(string credit) =>
        Assert.Equal(CreditVerdict.Unjudged, FileCredits.Judge([credit], Moonward));

    [Fact]
    public void A_blank_wanted_name_judges_nothing() =>
        Assert.Equal(CreditVerdict.Unjudged, FileCredits.Judge(["Salt Road"], new ArtistCredit("")));

    [Theory]
    [InlineData("Various Artists", CreditVerdict.Named)]
    [InlineData("Salt Road", CreditVerdict.Foreign)]
    public void A_various_artists_target_is_named_by_a_various_artists_credit(string albumArtist, CreditVerdict expected) =>
        Assert.Equal(expected, FileCredits.Judge([albumArtist, "Salt Road"], new ArtistCredit("Various Artists")));
}

public sealed class FileCreditsReadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sleezer-credits-" + Path.GetRandomFileName());

    public FileCreditsReadTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Flac(string[] albumArtists, string[] performers)
    {
        string path = Path.Combine(_dir, Path.GetRandomFileName() + ".flac");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "silence.flac"), path);
        using TagLib.File file = TagLib.File.Create(path);
        file.Tag.AlbumArtists = albumArtists;
        file.Tag.Performers = performers;
        file.Save();
        return path;
    }

    [Fact]
    public void Every_album_artist_and_artist_credit_is_read()
    {
        string path = Flac(["https://glassharbour.example"], ["Salt Road", "Moonward"]);

        Assert.Equal(["https://glassharbour.example", "Salt Road", "Moonward"], FileCredits.Read(path));
        Assert.Equal(CreditVerdict.Named, FileCredits.Judge(FileCredits.Read(path)!, new ArtistCredit("Moonward")));
    }

    [Fact]
    public void An_unreadable_file_has_no_credits()
    {
        string path = Path.Combine(_dir, "01 Lantern Song.flac");
        File.WriteAllText(path, "not audio");

        Assert.Null(FileCredits.Read(path));
    }
}
