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

    // A featured guest is not the file's artist, as in identification.
    [Fact]
    public void A_featured_guest_does_not_name_the_artist() =>
        Assert.Equal(CreditVerdict.Foreign, FileCredits.Judge(["Salt Road (feat. Moonward)"], Moonward));

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
