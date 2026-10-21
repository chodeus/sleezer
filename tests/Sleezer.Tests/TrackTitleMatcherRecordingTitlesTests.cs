using NzbDrone.Core.Music;
using NzbDrone.Plugin.Sleezer.Core.PostProcessing;
using Xunit;

namespace Sleezer.Tests;

// A single's recording often sits on its album under a longer title ("Song (Subtitle)");
// a file pulled from that album carries the album's title.
public class TrackTitleMatcherRecordingTitlesTests
{
    private const string WantedId = "be79672b-e88c-4f40-a36a-bf1200895d60";
    private const string OtherId = "31caa51e-648f-4353-8af4-a6ac8d44cd57";

    [Fact]
    public void A_file_matches_by_another_title_of_the_wanted_recording()
    {
        Assert.Empty(TrackTitleMatcher.Match(["Lantern Song (Quiet Hours)"], ["Lantern Song"]));

        Dictionary<int, int> mapping = TrackTitleMatcher.Match(["Lantern Song (Quiet Hours)"], [["Lantern Song", "Lantern Song (Quiet Hours)"]]);

        Assert.Equal(0, Assert.Single(mapping).Value);
    }

    [Theory]
    [InlineData("Lantern Song (Moonward remix)")]
    [InlineData("Lantern Song (Quiet Hours) (Moonward remix)")]
    [InlineData("Lantern Song (Quiet Hours) Part 2")]
    [InlineData("Lantern Song (Salt Road Tapes)")]
    public void Each_title_keeps_the_matching_rules(string local)
    {
        Assert.Empty(TrackTitleMatcher.Match([local], [["Lantern Song", "Lantern Song (Quiet Hours)"]]));
    }

    [Fact]
    public void A_pair_scores_by_its_best_title()
    {
        // Both tracks pass on their own titles alike; only the second carries the file's exact title.
        Dictionary<int, int> mapping = TrackTitleMatcher.Match(
            ["Moonward (Harbour)"],
            [["Moonward"], ["Moonward (Harbour Lantern Salt Road)", "Moonward (Harbour)"]]);

        Assert.Equal(1, Assert.Single(mapping).Value);
    }

    [Fact]
    public void Titles_are_the_tracks_own_then_the_other_titles_of_its_recording()
    {
        Track wanted = new() { Title = "Lantern Song", ForeignRecordingId = WantedId };
        Dictionary<string, IReadOnlyList<KnownTrack>> known = RecordingVerdict.KnownTracks(
            [WantedId, OtherId],
            [
                new Track { Title = "Lantern Song (Quiet Hours)", ForeignRecordingId = WantedId.ToUpperInvariant() },
                new Track { Title = "Lantern Song", ForeignRecordingId = WantedId },
                new Track { Title = "Lantern Song (Quiet Hours)", ForeignRecordingId = WantedId },
                new Track { Title = "Lantern Song (Moonward remix)", ForeignRecordingId = OtherId },
            ]);

        Assert.Equal(["Lantern Song", "Lantern Song (Quiet Hours)"], Assert.Single(TrackTitleMatcher.TitlesOf([wanted], known)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("31caa51e-648f-4353-8af4-a6ac8d44cd57")]
    public void A_recording_Lidarr_holds_no_titles_for_keeps_the_tracks_own(string? recordingId)
    {
        Track wanted = new() { Title = "Lantern Song", ForeignRecordingId = recordingId };
        Dictionary<string, IReadOnlyList<KnownTrack>> known = RecordingVerdict.KnownTracks([WantedId], [new Track { Title = "Lantern Song (Quiet Hours)", ForeignRecordingId = WantedId }]);

        Assert.Equal(["Lantern Song"], Assert.Single(TrackTitleMatcher.TitlesOf([wanted], known)));
    }

    // "Levels" names both the radio edit's recording and the original's on other releases: a file so titled could be either.
    [Theory]
    [InlineData("Glass Harbour", "Glass Harbour (Radio Edit)", "Glass Harbour (Original Version)")]
    [InlineData("glass harbour", "Glass Harbour (Radio Edit)", "Glass Harbour (Original Version)")]
    [InlineData("Glass Harbour", "Glass Harbour (Radio Edit)", "Glass Harbour")]
    public void A_title_two_wanted_recordings_share_places_neither(string sharedTitle, string first, string second)
    {
        Track[] wanted = [new() { Title = first, ForeignRecordingId = WantedId }, new() { Title = second, ForeignRecordingId = OtherId }];
        Dictionary<string, IReadOnlyList<KnownTrack>> known = RecordingVerdict.KnownTracks(
            [WantedId, OtherId],
            [
                new Track { Title = "Glass Harbour", ForeignRecordingId = WantedId },
                new Track { Title = sharedTitle, ForeignRecordingId = OtherId },
                new Track { Title = "Glass Harbour (Salt Road)", ForeignRecordingId = WantedId },
            ]);

        List<IReadOnlyList<string?>> titles = TrackTitleMatcher.TitlesOf(wanted, known);

        Assert.Equal([first, "Glass Harbour (Salt Road)"], titles[0]);
        Assert.Equal(second, Assert.Single(titles[1]));
    }

    [Fact]
    public void A_recording_listed_twice_keeps_its_other_titles()
    {
        Track[] wanted = [new() { Title = "Lantern Song", ForeignRecordingId = WantedId }, new() { Title = "Lantern Song", ForeignRecordingId = WantedId }];
        Dictionary<string, IReadOnlyList<KnownTrack>> known = RecordingVerdict.KnownTracks([WantedId], [new Track { Title = "Lantern Song (Quiet Hours)", ForeignRecordingId = WantedId }]);

        Assert.All(TrackTitleMatcher.TitlesOf(wanted, known), titles => Assert.Equal(["Lantern Song", "Lantern Song (Quiet Hours)"], titles));
    }
}
