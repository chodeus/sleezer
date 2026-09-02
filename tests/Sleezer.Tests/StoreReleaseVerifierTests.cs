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

        Assert.Empty(Apply(criteria, Store("Hardwell", "Chain Gang")));
    }

    [Theory]
    [InlineData("AFROJACK")]                  // case
    [InlineData("Afrojack & David Guetta")]   // collaboration credit still contains the artist whole
    [InlineData("The Afrojack")]              // leading article
    public void Artist_variations_are_accepted(string storeArtist)
    {
        var criteria = Criteria("Afrojack", "Chain Gang");

        Assert.Single(Apply(criteria, Store(storeArtist, "Chain Gang")));
    }

    [Fact]
    public void Artist_alias_is_accepted()
    {
        var criteria = Criteria("Chase & Status", "Boiler Room", aliases: ["Chase and Status"]);

        Assert.Single(Apply(criteria, Store("Chase and Status", "Boiler Room")));
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

        Assert.Single(Apply(criteria, Store("Ministry of Sound", "Ministry of Sound Annual")));
    }

    [Theory]
    [InlineData("chain gang")]
    [InlineData("Chain Gang (Deluxe Edition)")]   // an edition, not a variant
    [InlineData("Chain Gang [Remastered]")]
    public void Title_variations_that_are_the_same_album_pass(string storeTitle)
    {
        var criteria = Criteria("Afrojack", "Chain Gang");

        Assert.Single(Apply(criteria, Store("Afrojack", storeTitle)));
    }

    [Theory]
    [InlineData("Baby Get Shaky")]   // superset title is a different record
    [InlineData("Get Shaky (Macon's Remix)")]
    [InlineData("Get Shaky Remixes")]
    public void Different_titles_and_uncalled_for_remixes_are_dropped(string storeTitle)
    {
        var criteria = Criteria("The Ian Carey Project", "Get Shaky");

        Assert.Empty(Apply(criteria, Store("The Ian Carey Project", storeTitle)));
    }

    [Fact]
    public void Extended_mix_is_dropped_for_the_plain_album()
    {
        var criteria = Criteria("Afrojack", "Chain Gang");
        var release = Store("Afrojack", "Chain Gang", candidateTitle: "Chain Gang (Extended Mix)");

        Assert.Empty(Apply(criteria, release));
    }

    [Fact]
    public void Plain_album_is_dropped_when_the_search_is_for_the_remix()
    {
        var criteria = Criteria("Zomboy", "Valley of Violence (Borgore remix)");

        Assert.Empty(Apply(criteria, Store("Zomboy", "Valley of Violence")));
    }

    [Fact]
    public void Remix_is_accepted_when_the_album_calls_for_it()
    {
        var criteria = Criteria("Zomboy", "Valley of Violence (Borgore remix)");

        Assert.Single(Apply(criteria, Store("Zomboy", "Valley of Violence (Borgore Remix)")));
    }

    [Fact]
    public void Remix_is_accepted_when_musicbrainz_marks_the_album_as_remix()
    {
        var criteria = Criteria("Hardwell", "Apollo", secondaryTypes: [SecondaryAlbumType.Remix]);

        Assert.Single(Apply(criteria, Store("Hardwell", "Apollo (The Remixes)")));
    }

    [Theory]
    [InlineData(1, 2, false)]    // a single vs the two-track edition
    [InlineData(12, 13, true)]   // one bonus track
    [InlineData(12, 15, false)]  // three extra tracks
    public void Track_count_must_land_near_a_musicbrainz_release(int storeTracks, int mbTracks, bool kept)
    {
        var criteria = Criteria("Biscits", "Dominator", trackCount: mbTracks, durationSeconds: 0);
        var release = Store("Biscits", "Dominator", trackCount: storeTracks);

        Assert.Equal(kept ? 1 : 0, Apply(criteria, release).Count);
    }

    [Theory]
    [InlineData(108, 181, false)]  // the Chain Gang case: 1:48 offered for a 3:01 track
    [InlineData(175, 181, true)]
    [InlineData(3600, 3300, true)] // 5% on an hour-long album
    public void Duration_must_land_near_the_musicbrainz_release(int storeSeconds, int mbSeconds, bool kept)
    {
        var criteria = Criteria("Afrojack", "Chain Gang", trackCount: 1, durationSeconds: mbSeconds);
        var release = Store("Afrojack", "Chain Gang", trackCount: 1, durationSeconds: storeSeconds);

        Assert.Equal(kept ? 1 : 0, Apply(criteria, release).Count);
    }

    [Fact]
    public void Missing_store_data_is_unjudgeable_not_wrong()
    {
        var criteria = Criteria("Afrojack", "Chain Gang", trackCount: 1, durationSeconds: 181);
        var plain = new ReleaseInfo { Title = "Afrojack - Chain Gang (2023) [FLAC] [WEB]" };
        var partial = Store("Afrojack", "Chain Gang", trackCount: 0, durationSeconds: 0);

        Assert.Equal(2, Apply(criteria, plain, partial).Count);
    }

    [Fact]
    public void Interactive_search_shows_everything()
    {
        var criteria = Criteria("Afrojack", "Chain Gang");
        criteria.InteractiveSearch = true;

        Assert.Equal(2, Apply(criteria, Store("Hardwell", "Chain Gang"), Store("Afrojack", "Chain Gang (Extended Mix)")).Count);
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
