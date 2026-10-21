using NzbDrone.Core.Music;
using NzbDrone.Plugin.Sleezer.Core.PostProcessing;
using Xunit;
using static NzbDrone.Plugin.Sleezer.Core.PostProcessing.TrackTitleMatcher;

namespace Sleezer.Tests;

// A single's recording often sits on its album under a longer title ("Song (Subtitle)");
// a file pulled from that album carries the album's title.
public class TrackTitleMatcherRecordingTitlesTests
{
    private const string WantedId = "be79672b-e88c-4f40-a36a-bf1200895d60";
    private const string OtherId = "31caa51e-648f-4353-8af4-a6ac8d44cd57";

    private static Dictionary<string, IReadOnlyList<KnownTrack>> Known(params (string Id, string Title)[] tracks) =>
        RecordingVerdict.KnownTracks(tracks.Select(t => t.Id), [.. tracks.Select(t => new Track { ForeignRecordingId = t.Id, Title = t.Title })]);

    [Theory]
    [InlineData("Lantern Song (Quiet Hours)")]
    [InlineData("lantern song - quiet hours")]
    public void A_file_matches_by_another_title_of_the_wanted_recording(string local)
    {
        Assert.Empty(Match([local], ["Lantern Song"]));

        Dictionary<int, int> mapping = Match([local], [new WantedTitles("Lantern Song", ["Lantern Song (Quiet Hours)"])]);

        Assert.Equal(0, Assert.Single(mapping).Value);
    }

    // A fuzzy other title would draw in files of the release's other recordings.
    [Theory]
    [InlineData("Lantern Song (Quiet Hour)")]
    [InlineData("Lantern Song (Quiet Hours) (Moonward remix)")]
    [InlineData("Lantern Song (Quiet Hours) Part 2")]
    [InlineData("Lantern Song (Quiet Hours Salt Road)")]
    public void Another_title_matches_only_exactly(string local)
    {
        Assert.Empty(Match([local], [new WantedTitles("Lantern Song", ["Lantern Song (Quiet Hours)"])]));
    }

    [Fact]
    public void The_own_title_keeps_its_matching_rules()
    {
        WantedTitles wanted = new("Lantern Song", ["Lantern Song (Quiet Hours)"]);

        Assert.Equal(0, Assert.Single(Match(["Lantern Song (Quiet)"], [wanted])).Value);
        Assert.Empty(Match(["Lantern Song (Moonward remix)"], [wanted]));
        Assert.Equal(0, Assert.Single(Match(["Lantern Song"], [wanted])).Value);
    }

    [Fact]
    public void An_exact_other_title_outranks_a_looser_own_title()
    {
        Dictionary<int, int> mapping = Match(
            ["Moonward (Harbour)"],
            [new WantedTitles("Moonward", []), new WantedTitles("Moonward (Harbour Lantern Salt Road)", ["Moonward (Harbour)"])]);

        Assert.Equal(1, Assert.Single(mapping).Value);
    }

    [Fact]
    public void A_recording_listed_twice_keeps_each_file_at_its_own_title()
    {
        Track[] wanted = [new() { Title = "Glass Pier", ForeignRecordingId = WantedId }, new() { Title = "Glass Pier (Reprise)", ForeignRecordingId = WantedId }];
        List<WantedTitles> titles = TitlesOf(wanted, Known((WantedId, "Glass Pier"), (WantedId, "Glass Pier (Reprise)")));

        Dictionary<int, int> mapping = Match(["Glass Pier (Reprise)", "Glass Pier"], titles);

        Assert.Equal(new Dictionary<int, int> { [0] = 1, [1] = 0 }, mapping);
    }

    [Fact]
    public void Titles_are_the_tracks_own_then_the_other_titles_of_its_recording()
    {
        Track wanted = new() { Title = "Lantern Song", ForeignRecordingId = WantedId };
        var known = Known(
            (WantedId.ToUpperInvariant(), "Lantern Song (Quiet Hours)"),
            (WantedId, "Lantern Song"),
            (WantedId, "Lantern Song (Quiet Hours)"),
            (OtherId, "Lantern Song (Moonward remix)"));

        WantedTitles titles = Assert.Single(TitlesOf([wanted], known));

        Assert.Equal("Lantern Song", titles.Title);
        Assert.Equal(["Lantern Song (Quiet Hours)"], titles.OtherTitles);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("31caa51e-648f-4353-8af4-a6ac8d44cd57")]
    public void A_recording_Lidarr_holds_no_titles_for_keeps_the_tracks_own(string? recordingId)
    {
        Track wanted = new() { Title = "Lantern Song", ForeignRecordingId = recordingId };

        Assert.Empty(Assert.Single(TitlesOf([wanted], Known((WantedId, "Lantern Song (Quiet Hours)")))).OtherTitles);
    }

    // A compilation can list a remix's recording under the plain title, which names any plain file.
    [Theory]
    [InlineData("Ember Line (Moonward Remix)", "Ember Line", false)]
    [InlineData("Ember Line (Radio Edit)", "Ember Line", false)]
    [InlineData("Ember Line (Extended Mix)", "Ember Line (Salt Road)", false)]
    [InlineData("Ember Line (Moonward Remix)", "Ember Line (Moonward Remix) (Salt Road)", true)]
    [InlineData("Ember Line", "Ember Line (Radio Edit)", true)]
    public void A_plain_title_never_stands_in_for_a_variant(string own, string other, bool kept)
    {
        Track wanted = new() { Title = own, ForeignRecordingId = WantedId };

        IReadOnlyList<string> others = Assert.Single(TitlesOf([wanted], Known((WantedId, other)))).OtherTitles;

        Assert.Equal(kept, others.Contains(other));
    }

    // The last row's plain title is no use to its remix, yet still says a file so titled could be the remix.
    [Theory]
    [InlineData("Glass Harbour (Salt Road)", "Glass Harbour (Lantern)")]
    [InlineData("glass harbour - salt road", "Glass Harbour (Lantern)")]
    [InlineData("Glass Harbour (Ember)", "Glass Harbour (Salt Road)")]
    [InlineData("Glass Harbour (Salt Road)", "Glass Harbour (Moonward Remix)")]
    public void An_other_title_a_second_wanted_recording_carries_is_left_out(string secondsOther, string second)
    {
        Track[] wanted = [new() { Title = "Glass Harbour", ForeignRecordingId = WantedId }, new() { Title = second, ForeignRecordingId = OtherId }];
        var known = Known((WantedId, "Glass Harbour (Salt Road)"), (OtherId, secondsOther), (WantedId, "Glass Harbour (Quiet Hours)"));

        List<WantedTitles> titles = TitlesOf(wanted, known);

        Assert.Equal(["Glass Harbour", second], titles.Select(t => t.Title));
        Assert.Equal(["Glass Harbour (Quiet Hours)"], titles[0].OtherTitles);
    }

    [Theory]
    [InlineData(WantedId)]
    [InlineData("BE79672B-E88C-4F40-A36A-BF1200895D60")]
    public void A_recording_listed_twice_keeps_its_other_titles(string secondId)
    {
        Track[] wanted = [new() { Title = "Lantern Song", ForeignRecordingId = WantedId }, new() { Title = "Lantern Song", ForeignRecordingId = secondId }];

        Assert.All(TitlesOf(wanted, Known((WantedId, "Lantern Song (Quiet Hours)"))), titles => Assert.Equal(["Lantern Song (Quiet Hours)"], titles.OtherTitles));
    }
}
