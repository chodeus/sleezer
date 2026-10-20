using System.Text.Json;
using NLog;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Music;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek.Search.Core;
using Xunit;

namespace Sleezer.Tests;

// MusicBrainz names a variant in the disambiguation ("Instrumentals", "Huglife remix") while the title stays plain.
public class DisambiguationVariantTests
{
    private static Album Album(string title, string? disambiguation, params string[] tracks) => new()
    {
        Title = title,
        Disambiguation = disambiguation,
        AlbumReleases = new LazyLoaded<List<AlbumRelease>>(
            [new AlbumRelease { Monitored = true, Tracks = new LazyLoaded<List<Track>>([.. tracks.Select(t => new Track { Title = t })]) }]),
    };

    [Fact]
    public void An_instrumentals_edition_carries_its_disambiguation() =>
        Assert.Equal("Glass Harbour (Instrumentals)",
            VariantQualifiers.VariantTitleOf(Album("Glass Harbour", "Instrumentals", "Salt Road (instrumental)", "Lantern Song (instrumental)"), "Glass Harbour"));

    [Theory]
    [InlineData("Moonward remix", "Lantern Song (Moonward remix)")]
    [InlineData("acoustic", "Lantern Song (acoustic)")]
    [InlineData("live", "Lantern Song (live)")]
    public void A_variant_the_tracks_carry_is_added(string disambiguation, string track) =>
        Assert.Equal($"Lantern Song ({disambiguation})", VariantQualifiers.VariantTitleOf(Album("Lantern Song", disambiguation, track), "Lantern Song"));

    // A "club mix" is no remix keyword: exactly half the tracks carry the variant.
    [Fact]
    public void Half_the_tracks_carrying_the_variant_is_enough() =>
        Assert.NotNull(VariantQualifiers.VariantTitleOf(Album("Lantern Song", "remix bundle", "Lantern Song (Moonward remix)", "Lantern Song (Salt Road club mix)"), "Lantern Song"));

    [Fact]
    public void Fewer_than_half_the_tracks_carrying_the_variant_is_not() =>
        Assert.Null(VariantQualifiers.VariantTitleOf(Album("Lantern Song", "remixes", "Lantern Song", "Lantern Song (Moonward remix)", "Tidewater"), "Lantern Song"));

    // The artwork, an unofficial release, a demo the tracks don't name, or no variant at all.
    [Theory]
    [InlineData("blue cover", "Glass Harbour")]
    [InlineData("bootleg", "Lantern Song")]
    [InlineData("8-track demo", "Lantern Song")]
    [InlineData("UK release", "Lantern Song (Moonward remix)")]
    [InlineData("", "Lantern Song (Moonward remix)")]
    [InlineData(null, "Lantern Song (Moonward remix)")]
    public void A_disambiguation_the_tracks_do_not_carry_adds_nothing(string? disambiguation, string track) =>
        Assert.Null(VariantQualifiers.VariantTitleOf(Album("Glass Harbour", disambiguation, track), "Glass Harbour"));

    // A remix EP lists its original and its edits beside the remixes; a narrower word needs tracks naming it.
    [Fact]
    public void Remixes_are_borne_out_by_remixes_and_edits_together() =>
        Assert.NotNull(VariantQualifiers.VariantTitleOf(
            Album("Lantern Song", "remixes", "Lantern Song (original mix)", "Lantern Song (Moonward remix)", "Lantern Song (radio edit)", "Lantern Song (Salt Road remix)", "Lantern Song (DJ edit)"), "Lantern Song"));

    [Theory]
    [InlineData("Lantern Song (radio edit)", false)]
    [InlineData("Lantern Song (Moonward bootleg)", true)]
    public void A_bootleg_is_borne_out_only_by_bootleg_tracks(string track, bool added) =>
        Assert.Equal(added, VariantQualifiers.VariantTitleOf(Album("Lantern Song", "UA bootleg", track, track), "Lantern Song") != null);

    // A dash inside the disambiguation hides nothing: its kinds come from the whole text.
    [Theory]
    [InlineData("UA bootleg - blue vinyl", "Lantern Song (radio edit)")]
    [InlineData("instrumentals - promo", "Lantern Song (Moonward remix)")]
    public void A_dash_in_the_disambiguation_keeps_its_kind(string disambiguation, string track) =>
        Assert.Null(VariantQualifiers.VariantTitleOf(Album("Lantern Song", disambiguation, track, track), "Lantern Song"));

    [Fact]
    public void A_dashed_disambiguation_its_tracks_bear_out_is_added() =>
        Assert.Equal("Lantern Song (Moonward bootleg - blue vinyl)",
            VariantQualifiers.VariantTitleOf(Album("Lantern Song", "Moonward bootleg - blue vinyl", "Lantern Song (Moonward bootleg)"), "Lantern Song"));

    // A title's dash tail takes the disambiguation into its remix name, but "UK release" names no kind.
    [Fact]
    public void A_disambiguation_naming_no_kind_adds_nothing_to_a_dashed_remix_title() =>
        Assert.Null(VariantQualifiers.VariantTitleOf(Album("Lantern Song - Moonward Remix", "UK release", "Lantern Song (Moonward remix)"), "Lantern Song - Moonward Remix"));

    // "Dub" in the title already reads as a remix word; the disambiguation's "Instrumentals" is still a new kind.
    [Fact]
    public void A_kind_word_in_the_title_does_not_hide_the_disambiguation() =>
        Assert.Equal("Dub Cathedral (Instrumentals)",
            VariantQualifiers.VariantTitleOf(Album("Dub Cathedral", "Instrumentals", "Salt Road (instrumental)", "Lantern Song (instrumental)"), "Dub Cathedral"));

    [Fact]
    public void An_unknown_tracklist_adds_nothing() =>
        Assert.Null(VariantQualifiers.VariantTitleOf(new Album { Title = "Glass Harbour", Disambiguation = "Instrumentals" }, "Glass Harbour"));

    [Fact]
    public void The_plain_release_conflicts_with_the_variant_title_and_the_variant_does_not()
    {
        Album album = Album("Glass Harbour", "Instrumentals", "Salt Road (instrumental)", "Lantern Song (instrumental)");
        string wanted = VariantQualifiers.VariantTitleOf(album, album.Title)!;

        Assert.True(VariantQualifiers.RemixSignaturesConflict(wanted, "Glass Harbour", VariantQualifiers.ForgivenVariants(album)));
        Assert.False(VariantQualifiers.RemixSignaturesConflict(wanted, "Glass Harbour (Instrumentals)", VariantQualifiers.ForgivenVariants(album)));
    }

    // A generic label forgives only its own kind: an instrumental is no remix, nor an acapella an instrumental.
    [Theory]
    [InlineData("Glass Harbour (Instrumentals)", "Glass Harbour (Acapellas)", true)]
    [InlineData("Glass Harbour (Instrumentals)", "Glass Harbour (Tidewater Remix)", true)]
    [InlineData("Lantern Song (Moonward remix)", "Lantern Song (Instrumental)", true)]
    [InlineData("Lantern Song (Remixes)", "Lantern Song (Slowed + Reverb)", true)]
    [InlineData("Lantern Song (Acapella)", "Lantern Song (Moonward Remix)", true)]
    [InlineData("Lantern Song (Karaoke)", "Lantern Song (Remixes)", true)]
    [InlineData("Glass Harbour (Instrumentals)", "Glass Harbour (Instrumental Version)", false)]
    [InlineData("Lantern Song (Remixes)", "Lantern Song (Moonward Remix)", false)]
    [InlineData("Lantern Song (Remixes)", "Lantern Song (Radio Edit)", false)]
    [InlineData("Lantern Song (Remixes)", "Lantern Song (Moonward Bootleg)", false)]
    public void A_variant_label_forgives_only_its_own_kind(string wanted, string candidate, bool conflicts) =>
        Assert.Equal(conflicts, VariantQualifiers.RemixSignaturesConflict(wanted, candidate));

    // Kinds are read where the signature is: the qualifier, not a remix word in the base title.
    [Fact]
    public void A_kind_word_in_the_base_title_is_not_the_qualifier() =>
        Assert.True(VariantQualifiers.RemixSignaturesConflict("Dub Cathedral (Instrumental)", "Dub Cathedral (Moonward Remix)"));

    // Each folder counts on its own: the parent's dash must not hide the leaf's "Instrumentals".
    [Fact]
    public void A_parent_folder_does_not_hide_the_leaf_folders_kind() =>
        Assert.False(VariantQualifiers.RemixSignaturesConflict("Glass Harbour (Instrumentals)", ["Glass Harbour Instrumentals", "Moonward - Dub Rarities"], null));

    // Mirrors SlskdFolderVerdictTests.Judge.
    private static SlskdFolderVerdict Judge(string folder, SlskdSearchData search)
    {
        List<SlskdFileData> files = [new SlskdFileData($@"{folder}\01 Lantern Song.flac", null, 16, 30_000_000, 210, ".flac", 44100, 0, false)];
        return new SlskdItemsParser(LogManager.GetLogger("tests")).JudgeFolder(files.GroupBy(f => SlskdTextProcessor.GetMergedDirectoryKey(f.Filename)).Single(), search, null, 0);
    }

    [Fact]
    public void A_slskd_folder_named_plainly_is_refused_for_the_variant_album()
    {
        SlskdSearchData search = new("Moonward", "Glass Harbour", false, false, 1, null, VariantAlbum: "Glass Harbour (Instrumentals)");

        Assert.Contains("remix or version qualifier differs", Judge(@"Music\Moonward\Glass Harbour", search).Reasons);
        Assert.True(Judge(@"Music\Moonward\Glass Harbour (Instrumentals)", search).Matched);
    }

    [Fact]
    public void Without_a_variant_title_the_plain_folder_still_matches() =>
        Assert.True(Judge(@"Music\Moonward\Glass Harbour", new SlskdSearchData("Moonward", "Glass Harbour", false, false, 1, null)).Matched);

    // The search request carries the variant title to the parser as JSON.
    [Fact]
    public void The_variant_title_survives_the_search_request()
    {
        SearchContext context = new("Moonward", "Glass Harbour", "2020", false, 2, [], [], new SlskdSettings(), []) { VariantAlbum = "Glass Harbour (Instrumentals)" };
        SearchQuery query = SearchQuery.FromContext(context);
        string json = JsonSerializer.Serialize(new SlskdSearchData("Moonward", query.Album, false, false, 1, null, VariantAlbum: query.VariantAlbum));

        Assert.Equal("Glass Harbour (Instrumentals)", SlskdSearchData.FromJson(json).VariantAlbum);
    }
}
