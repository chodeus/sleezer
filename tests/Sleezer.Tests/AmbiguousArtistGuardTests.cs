using NLog;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Core.Model;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using Xunit;

namespace Sleezer.Tests;

// Two library artists sharing a CleanName make Lidarr's FindByName throw for any result credited to
// that name, and the whole search comes back as a 500 — the wanted album included.
public class AmbiguousArtistGuardTests
{
    private static readonly Logger Log = LogManager.CreateNullLogger();

    private static Artist A(string name) => new() { Name = name, CleanName = name.ToLowerInvariant().Replace(" ", string.Empty) };

    private static StoreReleaseInfo R(string artist, string album, params string[] mainArtists)
    {
        StoreReleaseInfo release = new() { Artist = artist, Album = album, MainArtists = mainArtists };
        // The shape QobuzParser emits; Lidarr's title parser needs the year and format tags.
        ReleaseTitle.Compose(release, artist, album, " (2024) [Lossless] [WEB]");
        return release;
    }

    private static readonly Artist Searched = A("Main Act");

    [Fact]
    public void Leaves_results_alone_when_no_library_name_is_duplicated()
    {
        List<ReleaseInfo> releases = [R("Guest Act", "Remix Pack", "Main Act", "Guest Act")];

        Assert.Same(releases, AmbiguousArtistGuard.Apply(releases, Searched, [A("Main Act"), A("Guest Act")], "Store", Log));
    }

    [Fact]
    public void Retitles_under_the_searched_artist_when_the_store_credits_them_as_main()
    {
        StoreReleaseInfo release = R("Guest Act", "Remix Pack", "Main Act", "Guest Act");

        IList<ReleaseInfo> kept = AmbiguousArtistGuard.Apply([release], Searched, [A("Main Act"), A("Guest Act"), A("Guest Act")], "Store", Log);

        Assert.Same(release, Assert.Single(kept));
        Assert.Equal("Main Act - Remix Pack (2024) [Lossless] [WEB]", release.Title);
        Assert.Equal("Main Act", release.Artist);
    }

    [Fact]
    public void Drops_a_result_whose_credited_artist_is_duplicated_and_not_the_searched_one()
    {
        IList<ReleaseInfo> kept = AmbiguousArtistGuard.Apply(
            [R("Guest Act", "Solo Album", "Guest Act")], Searched, [A("Main Act"), A("Guest Act"), A("Guest Act")], "Store", Log);

        Assert.Empty(kept);
    }

    [Fact]
    public void Keeps_results_credited_to_the_searched_artist_even_when_that_name_is_duplicated()
    {
        StoreReleaseInfo release = R("Main Act", "Own Album", "Main Act");

        IList<ReleaseInfo> kept = AmbiguousArtistGuard.Apply([release], Searched, [A("Main Act"), A("Main Act")], "Store", Log);

        Assert.Same(release, Assert.Single(kept));
        Assert.Equal("Main Act - Own Album (2024) [Lossless] [WEB]", release.Title);
    }

    // Two VA entries in the library are the common case: the guard drops the duplicated credit on
    // both search paths, and StoreReleaseVerifier drops any VA hit on an album search. Never retitled.
    [Fact]
    public void Never_retitles_a_various_artists_compilation()
    {
        IList<ReleaseInfo> kept = AmbiguousArtistGuard.Apply(
            [R("Various Artists", "Hits", "Main Act", "Guest Act")], Searched, [A("Main Act"), A("Various Artists"), A("Various Artists")], "Store", Log);

        Assert.Empty(kept);
    }

    // Through StoreResultRefiner, which SleezerHttpIndexerBase.Fetch calls: with the verifier first
    // the retitled result keeps its artist-mismatch Rejection and StoreMatchSpecification drops it.
    [Fact]
    public void A_retitled_result_passes_verification_when_the_guard_runs_first()
    {
        StoreReleaseInfo release = R("Guest Act", "Remix Pack", "Main Act", "Guest Act");
        Artist searched = A("Main Act");
        searched.Metadata = new LazyLoaded<ArtistMetadata>(new ArtistMetadata { Name = searched.Name, Aliases = [] });
        var criteria = new AlbumSearchCriteria
        {
            Artist = searched,
            AlbumTitle = "Remix Pack",
            Albums = [new Album { Title = "Remix Pack", SecondaryTypes = [], AlbumReleases = new LazyLoaded<List<AlbumRelease>>([]) }],
        };

        IList<ReleaseInfo> refined = StoreResultRefiner.Refine(
            [release], criteria, strictMatching: true, AmbiguousArtistGuard.DuplicatedCleanNames([A("Main Act"), A("Guest Act"), A("Guest Act")]), "Store", Log);

        Assert.Same(release, Assert.Single(refined));
        Assert.Equal("Main Act", release.Artist);
        Assert.Null(release.Rejection);
    }

    // Slskd emits plain ReleaseInfo with a folder-derived artist: nothing to retitle from, so a
    // colliding name is dropped, while a result stamped with the searched artist stays.
    [Fact]
    public void A_plain_release_under_a_colliding_folder_artist_is_dropped_and_the_searched_one_kept()
    {
        ReleaseInfo folder = new() { Title = "Guest Act - Some Album (2024) [FLAC]", Artist = "Guest Act", Album = "Some Album" };
        ReleaseInfo stamped = new() { Title = "Main Act - Some Album (2024) [FLAC]", Artist = "Main Act", Album = "Some Album" };

        IList<ReleaseInfo> kept = AmbiguousArtistGuard.Apply([folder, stamped], Searched, [A("Main Act"), A("Guest Act"), A("Guest Act")], "Slskd", Log);

        Assert.Same(stamped, Assert.Single(kept));
    }
}
