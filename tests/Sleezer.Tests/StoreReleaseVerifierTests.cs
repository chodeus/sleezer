using NLog;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Core.Model;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using Xunit;

namespace Sleezer.Tests;

public class StoreReleaseVerifierTests
{
    private static readonly Logger Logger = LogManager.GetLogger(nameof(StoreReleaseVerifierTests));

    // Control: an exact store hit for the searched album passes untouched.
    [Fact]
    public void Exact_match_passes()
    {
        var criteria = Criteria("Afrojack", "Chain Gang", trackCount: 1, durationSeconds: 108);
        var release = Store("Afrojack", "Chain Gang", trackCount: 1, durationSeconds: 108);

        Assert.Same(release, Assert.Single(Apply(criteria, release)));
    }

    [Fact]
    public void Wrong_artist_is_dropped()
    {
        var criteria = Criteria("Afrojack", "Chain Gang");

        Assert.True(Rejected(criteria, Store("Hardwell", "Chain Gang")));
    }

    [Theory]
    [InlineData("AFROJACK")]                  // case
    [InlineData("Afrojack & David Guetta")]   // collaboration credit still contains the artist whole
    [InlineData("The Afrojack")]              // leading article
    public void Artist_variations_are_accepted(string storeArtist)
    {
        var criteria = Criteria("Afrojack", "Chain Gang");

        Assert.True(Accepted(criteria, Store(storeArtist, "Chain Gang")));
    }

    [Fact]
    public void Artist_alias_is_accepted()
    {
        var criteria = Criteria("Chase & Status", "Boiler Room", aliases: ["Chase and Status"]);

        Assert.True(Accepted(criteria, Store("Chase and Status", "Boiler Room")));
    }

    [Fact]
    public void Various_artists_result_is_dropped_for_a_named_artist()
    {
        var criteria = Criteria("Afrojack", "Chain Gang");

        Assert.Empty(Apply(criteria, Store("Various Artists", "Chain Gang")));
    }

    [Fact]
    public void Various_artists_target_accepts_any_credit()
    {
        var criteria = Criteria("Various Artists", "Ministry of Sound Annual");

        Assert.True(Accepted(criteria, Store("Ministry of Sound", "Ministry of Sound Annual")));
    }

    [Theory]
    [InlineData("chain gang")]
    [InlineData("Chain Gang (Deluxe Edition)")]   // an edition, not a variant
    [InlineData("Chain Gang [Remastered]")]
    public void Title_variations_that_are_the_same_album_pass(string storeTitle)
    {
        var criteria = Criteria("Afrojack", "Chain Gang");

        Assert.True(Accepted(criteria, Store("Afrojack", storeTitle)));
    }

    [Theory]
    [InlineData("Baby Get Shaky")]   // superset title is a different record
    [InlineData("Get Shaky (Macon's Remix)")]
    [InlineData("Get Shaky Remixes")]
    public void Different_titles_and_uncalled_for_remixes_are_dropped(string storeTitle)
    {
        var criteria = Criteria("The Ian Carey Project", "Get Shaky");

        Assert.True(Rejected(criteria, Store("The Ian Carey Project", storeTitle)));
    }

    [Fact]
    public void Extended_mix_is_dropped_for_the_plain_album()
    {
        var criteria = Criteria("Afrojack", "Chain Gang");
        var release = Store("Afrojack", "Chain Gang", candidateTitle: "Chain Gang (Extended Mix)");

        Assert.True(Rejected(criteria, release));
    }

    [Fact]
    public void Plain_album_is_dropped_when_the_search_is_for_the_remix()
    {
        var criteria = Criteria("Zomboy", "Valley of Violence (Borgore remix)");

        Assert.True(Rejected(criteria, Store("Zomboy", "Valley of Violence")));
    }

    [Fact]
    public void Remix_is_accepted_when_the_album_calls_for_it()
    {
        var criteria = Criteria("Zomboy", "Valley of Violence (Borgore remix)");

        Assert.True(Accepted(criteria, Store("Zomboy", "Valley of Violence (Borgore Remix)")));
    }

    [Fact]
    public void Remix_is_accepted_when_musicbrainz_marks_the_album_as_remix()
    {
        var criteria = Criteria("Hardwell", "Apollo", secondaryTypes: [SecondaryAlbumType.Remix]);

        Assert.True(Accepted(criteria, Store("Hardwell", "Apollo (The Remixes)")));
    }

    [Theory]
    [InlineData(1, 2, false)]    // a single vs the two-track edition
    [InlineData(12, 13, true)]   // one bonus track
    [InlineData(12, 15, false)]  // three extra tracks
    public void Track_count_must_land_near_a_musicbrainz_release(int storeTracks, int mbTracks, bool kept)
    {
        var criteria = Criteria("Biscits", "Dominator", trackCount: mbTracks, durationSeconds: 0);
        var release = Store("Biscits", "Dominator", trackCount: storeTracks);

        Assert.Equal(kept, Accepted(criteria, release));
    }

    [Theory]
    [InlineData(108, 181, false)]  // the Chain Gang case: 1:48 offered for a 3:01 track
    [InlineData(175, 181, true)]
    [InlineData(3600, 3300, true)] // 5% on an hour-long album
    public void Duration_must_land_near_the_musicbrainz_release(int storeSeconds, int mbSeconds, bool kept)
    {
        var criteria = Criteria("Afrojack", "Chain Gang", trackCount: 1, durationSeconds: mbSeconds);
        var release = Store("Afrojack", "Chain Gang", trackCount: 1, durationSeconds: storeSeconds);

        Assert.Equal(kept, Accepted(criteria, release));
    }

    [Fact]
    public void Missing_store_data_is_unjudgeable_not_wrong()
    {
        var criteria = Criteria("Afrojack", "Chain Gang", trackCount: 1, durationSeconds: 181);
        var plain = new ReleaseInfo { Title = "Afrojack - Chain Gang (2023) [FLAC] [WEB]" };
        var partial = Store("Afrojack", "Chain Gang", trackCount: 0, durationSeconds: 0);

        Assert.Equal(2, Apply(criteria, plain, partial).Count);
    }

    // Lidarr owns the interactive/automatic distinction now: both modes get the same list,
    // and the reason rides along for StoreMatchSpecification to reject on.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Failed_results_are_returned_with_a_reason_in_both_modes(bool interactive)
    {
        var criteria = Criteria("Afrojack", "Chain Gang");
        criteria.InteractiveSearch = interactive;
        var extendedMix = Store("Afrojack", "Chain Gang", candidateTitle: "Chain Gang (Extended Mix)");

        var kept = Apply(criteria, extendedMix);

        Assert.Same(extendedMix, Assert.Single(kept));
        Assert.False(string.IsNullOrEmpty(extendedMix.Rejection));
    }

    // A release from a non-store indexer carries no verdict and must pass through untouched.
    [Fact]
    public void A_plain_release_info_is_never_annotated()
    {
        var criteria = Criteria("Afrojack", "Chain Gang");
        var plain = new ReleaseInfo { Title = "x", Artist = "Hardwell", Album = "Chain Gang" };

        Assert.Same(plain, Assert.Single(Apply(criteria, plain)));
    }

    [Fact]
    public void Duration_is_judged_against_every_compatible_release()
    {
        // The store release is track-count compatible with both MusicBrainz editions;
        // it passes because its duration matches the 13-track one, not the nearest-count one.
        var criteria = Criteria("Artist", "Album", trackCount: 12, durationSeconds: 2500);
        criteria.Albums[0].AlbumReleases.Value.Add(new AlbumRelease { TrackCount = 13, Duration = 3300 * 1000 });

        Assert.True(Accepted(criteria, Store("Artist", "Album", trackCount: 12, durationSeconds: 3300)));
    }

    [Fact]
    public void Unknown_store_track_count_is_judged_against_all_releases()
    {
        var criteria = Criteria("Artist", "Album", trackCount: 1, durationSeconds: 200);
        criteria.Albums[0].AlbumReleases.Value.Add(new AlbumRelease { TrackCount = 12, Duration = 3000 * 1000 });

        Assert.True(Accepted(criteria, Store("Artist", "Album", trackCount: 0, durationSeconds: 3000)));
    }

    // Two "Various Artists" library entries make ArtistRepository.FindByName throw mid-search,
    // so VA hits for a named artist never reach Lidarr — interactive or not.
    [Fact]
    public void Interactive_search_still_drops_various_artists_for_a_named_artist()
    {
        var criteria = Criteria("Afrojack", "Chain Gang");
        criteria.InteractiveSearch = true;

        var kept = Apply(criteria, Store("Various Artists", "Chain Gang"), Store("Afrojack", "Chain Gang"));

        Assert.Equal("Afrojack", Assert.Single(kept).Artist);
    }

    private static IList<ReleaseInfo> Apply(AlbumSearchCriteria criteria, params ReleaseInfo[] releases) =>
        StoreReleaseVerifier.Apply(releases.ToList(), criteria, "Test", Logger);

    // A verified result comes back clean; a failed one comes back carrying the reason,
    // which StoreMatchSpecification turns into a Lidarr rejection.
    private static bool Accepted(AlbumSearchCriteria criteria, ReleaseInfo release)
    {
        var kept = Apply(criteria, release);
        return kept.Count == 1 && (kept[0] as StoreReleaseInfo)?.Rejection == null;
    }

    private static bool Rejected(AlbumSearchCriteria criteria, ReleaseInfo release)
    {
        var kept = Apply(criteria, release);
        return kept.Count == 1 && !string.IsNullOrEmpty((kept[0] as StoreReleaseInfo)?.Rejection);
    }

    // Live 2026-09-02: Oliver Heldens "Last All Night (Koala)" — the group title is
    // plain but its monitored release is six remix tracks, so the store's "(Remixes)"
    // product IS the album and must not be dropped as a variant.
    [Fact]
    public void A_remix_tracklist_vouches_for_a_remix_product()
    {
        var criteria = CriteriaWithTracklist(
            "Oliver Heldens",
            "Last All Night (Koala)",
            ["Last All Night (Koala) (Toyboy & Robin remix)",
             "Last All Night (Koala) (Toyboy & Robin dub)",
             "Last All Night (Koala) (Low Steppa remix)",
             "Last All Night (Koala) (TC4 remix)",
             "Last All Night (Koala) (Reso remix)"]);

        var release = Store("Oliver Heldens", "Last All Night (Koala)", trackCount: 5, candidateTitle: "Last All Night (Koala) (Remixes)");

        Assert.True(Accepted(criteria, release));
    }

    // Negative control for the rule above: a plain tracklist vouches for nothing, so an
    // uncalled-for remix product is still dropped.
    [Fact]
    public void A_plain_tracklist_does_not_vouch_for_a_remix_product()
    {
        var criteria = CriteriaWithTracklist(
            "Oliver Heldens",
            "Last All Night (Koala)",
            ["Last All Night (Koala)", "Koala", "Last All Night (Koala) (extended)",
             "Bunnydance", "Gecko"]);

        var release = Store("Oliver Heldens", "Last All Night (Koala)", trackCount: 5, candidateTitle: "Last All Night (Koala) (Remixes)");

        Assert.True(Rejected(criteria, release));
    }

    // Live 2026-09-02: Hardwell "Chase the Sun" — MusicBrainz held a 2-track edition, a
    // 1-track extended mix and a 1-track remix, but no plain 1-track single, so Lidarr
    // attached the correct plain download to the remix release and named it as the remix.
    [Fact]
    public void A_plain_product_is_rejected_when_only_variant_editions_fit()
    {
        var criteria = CriteriaWithReleases("Hardwell", "Chase the Sun",
            (["Chase the Sun (extended mix)", "Chase the Sun"], true),
            (["Chase the Sun (extended mix)"], false),
            (["Chase the Sun (Jac & Harri remix)"], false));

        Assert.True(Rejected(criteria, Store("Hardwell", "Chase the Sun", trackCount: 1)));
    }

    // False-positive control: a single whose only edition is a "(radio edit)" is a plain
    // product, not a variant one — an edit is the main track of most singles.
    [Fact]
    public void A_radio_edit_tracklist_is_not_a_variant_edition()
    {
        var criteria = CriteriaWithReleases("Whigfield", "Saturday Night",
            (["Saturday Night (radio edit)"], true));

        Assert.True(Accepted(criteria, Store("Whigfield", "Saturday Night", trackCount: 1)));
    }

    // Tidal, SubSonic and Bandcamp report no track count, and missing data is unjudgeable:
    // without a count the rule has nothing to fit and must not reject.
    [Fact]
    public void A_candidate_with_no_track_count_is_not_rejected_for_lacking_a_plain_edition()
    {
        var criteria = CriteriaWithReleases("Hardwell", "Chase the Sun",
            (["Chase the Sun (Jac & Harri remix)"], true));

        Assert.True(Accepted(criteria, Store("Hardwell", "Chase the Sun")));
    }

    // False-positive control: one bonus remix on an otherwise plain album must not make
    // the whole edition count as a variant.
    [Fact]
    public void One_bonus_remix_does_not_make_an_edition_variant_only()
    {
        var criteria = CriteriaWithReleases("Biscits", "algorhythm",
            (["algorhythm", "bringing me down", "always knew (Some Remix)"], true));

        Assert.True(Accepted(criteria, Store("Biscits", "algorhythm", trackCount: 3)));
    }

    private static AlbumSearchCriteria Criteria(
        string artist,
        string album,
        int trackCount = 0,
        int durationSeconds = 0,
        List<string>? aliases = null,
        List<SecondaryAlbumType>? secondaryTypes = null)
    {
        List<AlbumRelease> releases = trackCount > 0
            ? [new AlbumRelease { TrackCount = trackCount, Duration = durationSeconds * 1000, Monitored = true }]
            : [];

        return new AlbumSearchCriteria
        {
            Artist = new Artist
            {
                Name = artist,
                Metadata = new LazyLoaded<ArtistMetadata>(new ArtistMetadata { Name = artist, Aliases = aliases ?? [] }),
            },
            AlbumTitle = album,
            Albums =
            [
                new Album
                {
                    Title = album,
                    SecondaryTypes = secondaryTypes ?? [],
                    AlbumReleases = new LazyLoaded<List<AlbumRelease>>(releases),
                }
            ],
        };
    }

    // A monitored release carrying a real tracklist — the vouching path needs track titles.
    private static AlbumSearchCriteria CriteriaWithTracklist(string artist, string album, string[] trackTitles)
    {
        var criteria = Criteria(artist, album, trackCount: trackTitles.Length);
        var release = criteria.Albums[0].AlbumReleases.Value[0];
        release.Tracks = new LazyLoaded<List<Track>>([.. trackTitles.Select(t => new Track { Title = t })]);
        return criteria;
    }

    // Several releases, each with its own tracklist — the plain-edition rule reads them.
    private static AlbumSearchCriteria CriteriaWithReleases(string artist, string album, params (string[] Tracks, bool Monitored)[] releases)
    {
        var criteria = Criteria(artist, album);
        criteria.Albums[0].AlbumReleases = new LazyLoaded<List<AlbumRelease>>(
        [
            .. releases.Select(r => new AlbumRelease
            {
                TrackCount = r.Tracks.Length,
                Monitored = r.Monitored,
                Tracks = new LazyLoaded<List<Track>>([.. r.Tracks.Select(t => new Track { Title = t })]),
            })
        ]);
        return criteria;
    }

    private static StoreReleaseInfo Store(string artist, string album, int trackCount = 0, int durationSeconds = 0, string? candidateTitle = null) => new()
    {
        Title = $"{artist} - {album} (2023) [FLAC] [WEB]",
        Artist = artist,
        Album = album,
        CandidateTitle = candidateTitle ?? album,
        TrackCount = trackCount,
        TotalDurationSeconds = durationSeconds,
    };
}
