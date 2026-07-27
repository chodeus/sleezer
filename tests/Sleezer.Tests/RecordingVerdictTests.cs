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

    private static Dictionary<string, string> Known(params (string Id, string Title)[] entries) =>
        entries.ToDictionary(e => e.Id, e => e.Title, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Exact_recording_id_match_is_verified()
    {
        FingerprintVerdict v = RecordingVerdict.Resolve(
            new[] { WantedId }, WantedId, null, "Voices", Known());
        Assert.Equal(FingerprintVerdict.Verified, v);
    }

    [Fact]
    public void Merged_away_old_recording_id_is_verified()
    {
        FingerprintVerdict v = RecordingVerdict.Resolve(
            new[] { OtherId }, WantedId, new[] { OtherId }, "Voices", Known());
        Assert.Equal(FingerprintVerdict.Verified, v);
    }

    // Dimension: grabbed the single "Angel", the file was "Guardian Angel".
    // AcoustID returned the Guardian Angel recording with score 1.0 — the catch
    // this whole gate exists for, and it must survive every relaxation.
    [Fact]
    public void Different_title_on_a_known_recording_is_a_mismatch()
    {
        FingerprintVerdict v = RecordingVerdict.Resolve(
            new[] { OtherId }, WantedId, null, "Angel",
            Known((OtherId, "Guardian Angel")));
        Assert.Equal(FingerprintVerdict.Mismatch, v);
    }

    // Brooks: wanted "Voices (feat. TZAR)" whose id 404s in MusicBrainz; AcoustID
    // answered with the sibling "Voices" recording. Ambiguous, so defer — never
    // veto, but never green-light either.
    [Fact]
    public void Same_title_on_a_different_recording_defers_instead_of_vetoing()
    {
        FingerprintVerdict v = RecordingVerdict.Resolve(
            new[] { OtherId }, WantedId, null, "Voices (feat. TZAR)",
            Known((OtherId, "Voices")));
        Assert.Equal(FingerprintVerdict.Unverifiable, v);
    }

    // Lee Mvtthews: AcoustID mapped the audio to a recording credited to another
    // artist entirely — nothing Lidarr knows, so it decides nothing.
    [Fact]
    public void Recording_ids_unknown_to_lidarr_are_unverifiable()
    {
        FingerprintVerdict v = RecordingVerdict.Resolve(
            new[] { "4fb3bb38-2956-4551-b4f7-7ac7b70d3631" }, WantedId, null, "Come Down",
            Known((OtherId, "Something Else")));
        Assert.Equal(FingerprintVerdict.Unverifiable, v);
    }

    // Version qualifiers are identity, not credit: a plain 2008 recording must not
    // satisfy a "(Taylor's Version)" target.
    [Fact]
    public void Version_qualifiers_still_read_as_a_different_recording()
    {
        FingerprintVerdict v = RecordingVerdict.Resolve(
            new[] { OtherId }, WantedId, null, "Should've Said No (Taylor's Version)",
            Known((OtherId, "Should've Said No")));
        Assert.Equal(FingerprintVerdict.Mismatch, v);
    }

    [Fact]
    public void No_acoustid_answer_is_unverifiable()
    {
        Assert.Equal(FingerprintVerdict.Unverifiable,
            RecordingVerdict.Resolve(null, WantedId, null, "Voices", Known()));
        Assert.Equal(FingerprintVerdict.Unverifiable,
            RecordingVerdict.Resolve(Array.Empty<string>(), WantedId, null, "Voices", Known()));
    }

    [Fact]
    public void Featured_credit_differences_normalize_away()
    {
        Assert.Equal(RecordingVerdict.NormalizeTitle("Voices"),
                     RecordingVerdict.NormalizeTitle("Voices (feat. TZAR)"));
        Assert.NotEqual(RecordingVerdict.NormalizeTitle("Should've Said No"),
                        RecordingVerdict.NormalizeTitle("Should've Said No (Taylor's Version)"));
    }
}
