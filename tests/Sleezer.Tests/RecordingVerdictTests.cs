using NzbDrone.Core.Music;
using NzbDrone.Plugin.Sleezer.Core.PostProcessing;
using Xunit;

namespace Sleezer.Tests;

// Cases below are the real ones measured on a live instance 2026-07-28, where a
// bare recording-id comparison vetoed correct files: AcoustID answers with a
// different MusicBrainz recording of the same song, and Lidarr's metadata can hold
// a wanted id that 404s upstream.
public class RecordingVerdictTests
{
    private const string WantedId = "be79672b-e88c-4f40-a36a-bf1200895d60";
    private const string OtherId = "31caa51e-648f-4353-8af4-a6ac8d44cd57";

    private static Dictionary<string, IReadOnlyList<KnownTrack>> Known(params (string Id, string Title)[] entries) =>
        Timed([.. entries.Select(e => (e.Id, e.Title, 0))]);

    private static Dictionary<string, IReadOnlyList<KnownTrack>> Timed(params (string Id, string Title, int Seconds)[] entries) =>
        entries.GroupBy(e => e.Id).ToDictionary(
            g => g.Key,
            g => (IReadOnlyList<KnownTrack>)[.. g.Select(e => new KnownTrack(e.Title, e.Seconds * 1000))],
            StringComparer.OrdinalIgnoreCase);

    private static WantedRecording Wanted(string title, IReadOnlyList<string>? oldIds = null, int seconds = 0, params string[] credited) =>
        new(WantedId, oldIds, title, seconds * 1000, credited);

    private static FingerprintVerdict Resolve(IReadOnlyList<string>? acoustIds, WantedRecording wanted, Dictionary<string, IReadOnlyList<KnownTrack>>? known, int fileSeconds = 0) =>
        RecordingVerdict.Resolve(acoustIds, TimeSpan.FromSeconds(fileSeconds), wanted, known);

    [Fact]
    public void Exact_recording_id_match_is_verified()
    {
        Assert.Equal(FingerprintVerdict.Verified, Resolve(new[] { WantedId }, Wanted("Voices"), Known()));
    }

    [Fact]
    public void Merged_away_old_recording_id_is_verified()
    {
        Assert.Equal(FingerprintVerdict.Verified, Resolve(new[] { OtherId }, Wanted("Voices", new[] { OtherId }), Known()));
    }

    // Dimension: grabbed the single "Angel", the file was "Guardian Angel".
    // AcoustID returned the Guardian Angel recording with score 1.0 — the catch
    // this whole gate exists for, and it must survive every relaxation.
    [Fact]
    public void Different_title_on_a_known_recording_is_a_mismatch()
    {
        Assert.Equal(FingerprintVerdict.Mismatch, Resolve(new[] { OtherId }, Wanted("Angel"), Known((OtherId, "Guardian Angel"))));
    }

    // Brooks: wanted "Voices (feat. TZAR)" whose id 404s in MusicBrainz; AcoustID
    // answered with the sibling "Voices" recording. Ambiguous, so defer — never
    // veto, but never green-light either.
    [Fact]
    public void Same_title_on_a_different_recording_defers_instead_of_vetoing()
    {
        Assert.Equal(FingerprintVerdict.Unverifiable, Resolve(new[] { OtherId }, Wanted("Voices (feat. TZAR)"), Known((OtherId, "Voices"))));
    }

    // Lee Mvtthews: AcoustID mapped the audio to a recording credited to another
    // artist entirely — nothing Lidarr knows, so it decides nothing.
    [Fact]
    public void Recording_ids_unknown_to_lidarr_are_unverifiable()
    {
        Assert.Equal(FingerprintVerdict.Unverifiable,
            Resolve(new[] { "4fb3bb38-2956-4551-b4f7-7ac7b70d3631" }, Wanted("Come Down"), Known((OtherId, "Something Else"))));
    }

    // Version qualifiers are identity, not credit: a plain 2008 recording must not
    // satisfy a "(Taylor's Version)" target.
    [Fact]
    public void Version_qualifiers_still_read_as_a_different_recording()
    {
        Assert.Equal(FingerprintVerdict.Mismatch,
            Resolve(new[] { OtherId }, Wanted("Should've Said No (Taylor's Version)"), Known((OtherId, "Should've Said No"))));
    }

    [Fact]
    public void No_acoustid_answer_is_unverifiable()
    {
        Assert.Equal(FingerprintVerdict.Unverifiable, Resolve(null, Wanted("Voices"), Known()));
        Assert.Equal(FingerprintVerdict.Unverifiable, Resolve(Array.Empty<string>(), Wanted("Voices"), Known()));
    }

    [Fact]
    public void Featured_credit_differences_normalize_away()
    {
        Assert.Equal(RecordingVerdict.NormalizeTitle("Voices"),
                     RecordingVerdict.NormalizeTitle("Voices (feat. TZAR)"));
        Assert.NotEqual(RecordingVerdict.NormalizeTitle("Should've Said No"),
                        RecordingVerdict.NormalizeTitle("Should've Said No (Taylor's Version)"));
    }

    // Lidarr files one recording under a title per release; the first one read was often not the wanted one.
    [Fact]
    public void A_recording_filed_under_the_wanted_title_on_any_release_defers()
    {
        Dictionary<string, IReadOnlyList<KnownTrack>> known = Known((OtherId, "Lantern Song (radio edit)"), (OtherId, "Lantern Song"));

        Assert.Equal(FingerprintVerdict.Unverifiable, Resolve(new[] { OtherId }, Wanted("Lantern Song"), known));
    }

    [Fact]
    public void Known_tracks_hold_every_title_of_each_named_recording_and_no_other_recording()
    {
        const string UnnamedId = "0d1e2f3a-4b5c-4d6e-8f70-81a2b3c4d5e6";
        Track[] tracks =
        [
            new() { ForeignRecordingId = OtherId, Title = "Lantern Song", Duration = 200_000 },
            new() { ForeignRecordingId = OtherId, Title = "Lantern Song (radio edit)", Duration = 181_000 },
            new() { ForeignRecordingId = OtherId, Title = "" },
            new() { ForeignRecordingId = UnnamedId, Title = "Salt Road" },
        ];

        Dictionary<string, IReadOnlyList<KnownTrack>> known = RecordingVerdict.KnownTracks([OtherId.ToUpperInvariant()], tracks);

        Assert.Equal([OtherId], known.Keys);
        Assert.Equal([new KnownTrack("Lantern Song", 200_000), new KnownTrack("Lantern Song (radio edit)", 181_000)], known[OtherId]);
    }

    [Theory]
    [InlineData("Salt Road", FingerprintVerdict.Unverifiable)]
    [InlineData("Quiet Hours", FingerprintVerdict.Mismatch)]
    public void A_with_clause_naming_a_credited_artist_is_no_part_of_the_title(string guest, FingerprintVerdict expected)
    {
        WantedRecording wanted = Wanted("Lantern Song (with Salt Road) - Moonward Remix", credited: ["Glass Harbour", guest]);

        Assert.Equal(expected, Resolve(new[] { OtherId }, wanted, Known((OtherId, "Lantern Song (Moonward remix)"))));
    }

    [Theory]
    [InlineData("Lantern Song (Moonward remix)", "Lantern Song (Moonward)", FingerprintVerdict.Unverifiable)]
    [InlineData("Lantern Song - Moonward", "Lantern Song (Moonward Remix)", FingerprintVerdict.Unverifiable)]
    [InlineData("Lantern Song - Moonward Remix", "Lantern Song (Moonward)", FingerprintVerdict.Unverifiable)]
    [InlineData("Lantern Song – Moonward Remix", "Lantern Song (Moonward)", FingerprintVerdict.Unverifiable)]
    [InlineData("Lantern Song（Moonward Remix）", "Lantern Song (Moonward)", FingerprintVerdict.Unverifiable)]
    [InlineData("Lantern Song (Remix)", "Lantern Song", FingerprintVerdict.Mismatch)]
    [InlineData("Lantern Song - Remix", "Lantern Song", FingerprintVerdict.Mismatch)]
    [InlineData("Lantern Song (Moonward remix)", "Lantern Song", FingerprintVerdict.Mismatch)]
    [InlineData("Lantern Song (Extended Mix)", "Lantern Song (Extended Remix)", FingerprintVerdict.Mismatch)]
    [InlineData("Lantern Song (Edit)", "Lantern Song (Remix Edit)", FingerprintVerdict.Mismatch)]
    [InlineData("Lantern Song (Moonward)", "Lantern Song (Moonward Remix Edit)", FingerprintVerdict.Mismatch)]
    [InlineData("Lantern Song (Instrumental)", "Lantern Song (Instrumental Remix)", FingerprintVerdict.Mismatch)]
    [InlineData("Lantern Song (Club)", "Lantern Song (Club Remix)", FingerprintVerdict.Mismatch)]
    [InlineData("Lantern Song - Radio Edit", "Lantern Song - Radio Remix Edit", FingerprintVerdict.Mismatch)]
    public void A_remixer_named_without_the_word_remix_is_the_same_remix(string wantedTitle, string knownTitle, FingerprintVerdict expected)
    {
        Assert.Equal(expected, Resolve(new[] { OtherId }, Wanted(wantedTitle), Known((OtherId, knownTitle))));
    }

    [Theory]
    [InlineData("÷", "×", FingerprintVerdict.Mismatch)]
    [InlineData("÷", "÷", FingerprintVerdict.Unverifiable)]
    public void A_title_of_marks_only_compares_by_its_marks(string wantedTitle, string knownTitle, FingerprintVerdict expected)
    {
        Assert.Equal(expected, Resolve(new[] { OtherId }, Wanted(wantedTitle), Known((OtherId, knownTitle))));
    }

    [Fact]
    public void Title_matching_still_tells_a_named_remix_from_its_remixer()
    {
        Assert.NotEqual(RecordingVerdict.NormalizeTitle("Lantern Song (Moonward remix)"), RecordingVerdict.NormalizeTitle("Lantern Song (Moonward)"));
    }

    // Live 2026-10-21: AcoustID named the 155s original for three remix files of 155s, 173s and 176s.
    [Theory]
    [InlineData(173, 172, 155, FingerprintVerdict.Unverifiable)]
    [InlineData(175, 172, 155, FingerprintVerdict.Unverifiable)]
    [InlineData(176, 172, 155, FingerprintVerdict.Mismatch)]
    [InlineData(173, 172, 162, FingerprintVerdict.Unverifiable)]
    [InlineData(173, 172, 163, FingerprintVerdict.Mismatch)]
    [InlineData(173, 172, 0, FingerprintVerdict.Mismatch)]
    [InlineData(173, 0, 155, FingerprintVerdict.Mismatch)]
    [InlineData(0, 2, 155, FingerprintVerdict.Mismatch)]
    [InlineData(2, 0, 155, FingerprintVerdict.Mismatch)]
    public void A_recording_far_off_the_files_length_defers_only_when_the_file_fits_the_wanted_length(int fileSeconds, int wantedSeconds, int knownSeconds, FingerprintVerdict expected)
    {
        FingerprintVerdict verdict = Resolve(new[] { OtherId }, Wanted("Lantern Song (Moonward remix)", seconds: wantedSeconds),
            Timed((OtherId, "Lantern Song", knownSeconds)), fileSeconds);

        Assert.Equal(expected, verdict);
    }

    [Theory]
    [InlineData("Lantern Song (instrumental)", "Lantern Song", 224, FingerprintVerdict.Unverifiable)]
    [InlineData("Lantern Song (clean)", "Lantern Song (explicit)", 224, FingerprintVerdict.Unverifiable)]
    [InlineData("Lantern Song", "Lantern Song [Dirty]", 224, FingerprintVerdict.Unverifiable)]
    [InlineData("Lantern Song - Instrumental", "Lantern Song", 224, FingerprintVerdict.Unverifiable)]
    [InlineData("Lantern Song – Instrumental Version", "Lantern Song", 224, FingerprintVerdict.Unverifiable)]
    [InlineData("Lantern Song（clean）", "Lantern Song (explicit)", 224, FingerprintVerdict.Unverifiable)]
    [InlineData("Lantern Song - Clean Harbour", "Lantern Song (Harbour)", 224, FingerprintVerdict.Mismatch)]
    [InlineData("Lantern Song (instrumental)", "Lantern Song", 230, FingerprintVerdict.Mismatch)]
    [InlineData("Lantern Song (acoustic)", "Lantern Song", 224, FingerprintVerdict.Mismatch)]
    [InlineData("Quiet Hours (instrumental)", "Lantern Song", 224, FingerprintVerdict.Mismatch)]
    public void AcoustID_cannot_hear_an_instrumental_or_clean_cut_apart_from_the_vocal(string wantedTitle, string knownTitle, int fileSeconds, FingerprintVerdict expected)
    {
        FingerprintVerdict verdict = Resolve(new[] { OtherId }, Wanted(wantedTitle, seconds: 224), Timed((OtherId, knownTitle, 225)), fileSeconds);

        Assert.Equal(expected, verdict);
    }

    [Fact]
    public void The_mismatch_log_names_each_recording_by_its_titles_and_lengths()
    {
        Dictionary<string, IReadOnlyList<KnownTrack>> known = Timed((OtherId, "Lantern Song (radio edit)", 180), (OtherId, "Lantern Song (radio edit)", 181));

        string described = RecordingVerdict.DescribeMatches(new[] { OtherId, WantedId }, known);

        Assert.Equal($"{OtherId} \"Lantern Song (radio edit)\" (180s/181s); {WantedId} (not in Lidarr)", described);
    }

    [Fact]
    public void The_mismatch_log_says_when_acoustid_named_nothing()
    {
        Assert.Equal("no recordings", RecordingVerdict.DescribeMatches(null, Known()));
        Assert.Equal("no recordings", RecordingVerdict.DescribeMatches(Array.Empty<string>(), null));
    }
}
