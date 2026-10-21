using Xunit;
using static NzbDrone.Plugin.Sleezer.Core.PostProcessing.TrackTitleMatcher;

namespace Sleezer.Tests;

// Files write the release's names into their titles where MusicBrainz credits them apart:
// "Song (with Artist)", "Song – Artist", "Artist - Song", "Song - Album", "Song feat. Guest".
public class TrackTitleMatcherReleaseNamesTests
{
    private static readonly ReleaseNames Names = new(["Juniper Vale", "Ora Fenn"], "Glass Harbour");

    private static WantedTitles[] Tracks(params string[] titles) => [.. titles.Select(t => new WantedTitles(t, []))];

    [Theory]
    [InlineData("Lantern Song (with Juniper Vale)")]
    [InlineData("Lantern Song – Juniper Vale")]
    [InlineData("Lantern Song - Juniper Vale & Ora Fenn")]
    [InlineData("Juniper Vale - Lantern Song")]
    [InlineData("Lantern Song - Glass Harbour")]
    [InlineData("Lantern Song feat. Ora Fenn")]
    [InlineData("Juniper Vale - Lantern Song - Glass Harbour")]
    [InlineData("Lantern Song - Juniper Vale - Glass Harbour")]
    [InlineData("Juniper Vale - Ora Fenn - Lantern Song")]
    [InlineData("Juniper Vale - Lantern Song")]
    public void A_file_matches_by_its_title_without_the_release_names(string local)
    {
        Assert.Empty(Match([local], Tracks("Tide Clock", "Lantern Song")));

        Assert.Equal(1, Assert.Single(Match([local], Tracks("Tide Clock", "Lantern Song"), Names)).Value);
    }

    // Anything else around a dash may be the title itself ("Song - Salt Road").
    [Theory]
    [InlineData("Lantern Song - Salt Road")]
    [InlineData("Lantern Song (with Moss Rook)")]
    [InlineData("Lantern Song feat. Moss Rook")]
    [InlineData("Lantern Song - Juniper Vale & Moss Rook")]
    [InlineData("Moss Rook - Lantern Song")]
    public void A_name_the_release_does_not_supply_stays(string local)
    {
        Assert.Empty(Match([local], Tracks("Lantern Song"), Names));
    }

    // Fuzzy, these would pass: "Lantern" sits inside "Lantern Song", and an unbracketed remix name is not compared.
    [Theory]
    [InlineData("Juniper Vale - Lantern", "Lantern Song")]
    [InlineData("Juniper Vale - Lantern Song Moss Remix", "Lantern Song (Fern remix)")]
    [InlineData("Juniper Vale - Lantern (Extended Mix)", "Lantern Tide Harbour (extended mix)")]
    public void A_title_without_the_names_matches_only_exactly(string local, string wanted)
    {
        Assert.Empty(Match([local], Tracks(wanted), Names));
    }

    [Fact]
    public void A_title_without_the_names_matches_another_title_of_the_recording()
    {
        Dictionary<int, int> mapping = Match(["Juniper Vale - Lantern Song (Quiet Hours)"], [new WantedTitles("Lantern Song", ["Lantern Song (Quiet Hours)"])], Names);

        Assert.Equal(0, Assert.Single(mapping).Value);
    }

    // MusicBrainz titles some tracks with the release name in them ("Song - Album"); the least cut title is tried first.
    [Theory]
    [InlineData("Salt Road - Glass Harbour - Juniper Vale")]
    [InlineData("Juniper Vale - Salt Road - Glass Harbour")]
    public void A_track_title_holding_a_release_name_keeps_it(string local)
    {
        Dictionary<int, int> mapping = Match([local], Tracks("Tide Clock", "Salt Road - Glass Harbour"), Names);

        Assert.Equal(1, Assert.Single(mapping).Value);
    }

    [Fact]
    public void The_least_cut_title_is_tried_first()
    {
        Dictionary<int, int> mapping = Match(["Salt Road - Glass Harbour - Juniper Vale"], Tracks("Salt Road", "Salt Road - Glass Harbour"), Names);

        Assert.Equal(1, Assert.Single(mapping).Value);
    }

    // A deeper cut that is a track's own title beats a shallower one that is only another title of a mix.
    [Fact]
    public void An_own_title_outranks_another_title_at_any_cut()
    {
        Dictionary<int, int> mapping = Match(
            ["Salt Road - Juniper Vale - Glass Harbour"],
            [new WantedTitles("Salt Road", []), new WantedTitles("Salt Road (club mix)", ["Salt Road (Juniper Vale)"])],
            Names);

        Assert.Equal(0, Assert.Single(mapping).Value);
    }

    [Fact]
    public void An_own_title_outranks_another_title()
    {
        Dictionary<int, int> mapping = Match(["Juniper Vale - Lantern Song"], [new WantedTitles("Tide Clock", ["Lantern Song"]), new WantedTitles("Lantern Song", [])], Names);

        Assert.Equal(1, Assert.Single(mapping).Value);
    }

    [Fact]
    public void A_matched_file_keeps_its_track()
    {
        Dictionary<int, int> mapping = Match(["Juniper Vale - Lantern Song", "Lantern Song"], Tracks("Lantern Song", "Tide Clock"), Names);

        Assert.Equal(new Dictionary<int, int> { [1] = 0 }, mapping);
    }

    // Both halves are tracks of the release, so either may be the title.
    [Theory]
    [InlineData("Moon Rook - Lantern Song")]
    [InlineData("Tide Clock Harbour - Lantern Song")]
    public void A_half_that_is_a_track_title_stays(string local)
    {
        ReleaseNames names = new(["Moon Rook"], "Lantern Song");

        Assert.Empty(Match([local], Tracks("Moon Rook", "Tide Clock Harbour", "Lantern Song"), names));
    }

    [Theory]
    [InlineData("Juniper Vale - Glass Harbour")]
    [InlineData("Juniper Vale - Ora Fenn")]
    public void A_title_of_nothing_but_names_stays(string local)
    {
        Assert.Empty(Match([local], Tracks("Lantern Song"), Names));
    }

    [Fact]
    public void A_bare_feat_keeps_the_remix_after_it()
    {
        Dictionary<int, int> mapping = Match(["Lantern Song feat. Ora Fenn (Moss remix)"], Tracks("Lantern Song", "Lantern Song (Moss remix)"), Names);

        Assert.Equal(1, Assert.Single(mapping).Value);
    }

    // The credited artists can cost a MusicBrainz lookup.
    [Fact]
    public void The_credited_artists_are_not_read_when_every_file_matches()
    {
        int reads = 0;
        IEnumerable<string?> Credited()
        {
            reads++;
            yield return "Juniper Vale";
        }

        Match(["Lantern Song", "Tide Clock"], Tracks("Lantern Song", "Tide Clock"), new ReleaseNames(Credited(), "Glass Harbour"));

        Assert.Equal(0, reads);
    }

    [Fact]
    public void The_credited_artists_are_read_once_for_every_unmatched_file()
    {
        int reads = 0;
        IEnumerable<string?> Credited()
        {
            reads++;
            yield return "Juniper Vale";
        }

        Dictionary<int, int> mapping = Match(
            ["Juniper Vale - Lantern Song", "Tide Clock - Juniper Vale", "Salt Road (with Juniper Vale)"],
            Tracks("Lantern Song", "Tide Clock", "Salt Road"),
            new ReleaseNames(Credited(), "Glass Harbour"));

        Assert.Equal(3, mapping.Count);
        Assert.Equal(1, reads);
    }
}
