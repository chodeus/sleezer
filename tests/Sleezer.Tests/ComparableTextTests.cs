using NLog;
using NzbDrone.Plugin.Sleezer.Core.Model;
using NzbDrone.Plugin.Sleezer.Core.PostProcessing;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek;
using Xunit;

namespace Sleezer.Tests;

// ComparableText and the matchers that build on it: slskd folders and files, store titles, the tagger, remixers.
public class ComparableTextTests
{
    private static readonly SlskdItemsParser Parser = new(LogManager.GetLogger("tests"));

    [Theory]
    [InlineData("Lümen Dü Sol", "lumen du sol")]
    [InlineData("Brøken Æther", "broken aether")]
    [InlineData("Hi‐Fi Night", "hi fi night")]
    [InlineData("Salt & Pepper", "salt and pepper")]
    [InlineData("Ca$h Ka$h", "cash kash")]
    [InlineData("$20 Lantern Song", "20 lantern song")]
    [InlineData("GLASS STRAẞE", "glass strasse")]
    [InlineData("Moon’s Edge", "moons edge")]
    [InlineData("S.T.A.R.", "star")]
    [InlineData("S_T_A_R_", "star")]
    [InlineData("Lantern Song.FLAC", "lantern song flac")]
    [InlineData("Glass Harbour Vol.2", "glass harbour vol 2")]
    [InlineData("Dr. Tide", "dr tide")]
    [InlineData(@"Music\Moonward/Glass Harbour", "music moonward glass harbour")]
    [InlineData(null, "")]
    public void names_and_titles_reduce_to_one_comparable_form(string? input, string expected)
    {
        Assert.Equal(expected, ComparableText.Of(input));
    }

    [Fact]
    public void a_peer_folder_without_the_accents_normalizes_like_the_searched_name()
    {
        Assert.Equal(SlskdItemsParser.NormalizeString("Lumen Du Sol"), SlskdItemsParser.NormalizeString("Lümen Dü Sol"));
    }

    // FuzzySharp's PartialRatio scores "tiesta" inside this folder at 50 and TokenSortRatio at 20.
    [Fact]
    public void a_folder_holding_the_artist_word_for_word_matches_whatever_the_fuzzy_score()
    {
        Assert.True(FolderMatches(@"library\Tiesta\Tiesta - Tide of Something (Moonward Remix)", "Tiesta", "Tide of Something (Moonward remix)"));
    }

    [Fact]
    public void a_short_name_of_several_words_matches_when_the_folder_holds_them_in_order()
    {
        Assert.True(FolderMatches(@"Music\Zed\X Y", "Zed", "X Y"));
    }

    [Theory]
    [InlineData("Moonward_Glass Harbour_02_Lantern Song", "Lantern Song")]
    [InlineData("Moonward_Glass Harbour_01-12_Lantern Song", "Lantern Song")]
    [InlineData("Moonward - Glass Harbour [01-06] Lantern Song", "Lantern Song")]
    [InlineData("03 - Lantern Song (Harbour Version _ Instrumental)", "Lantern Song (Harbour Version _ Instrumental)")]
    [InlineData("102-moonward_feat._ivy-lantern_song_(harbour_54_remix)", "moonward_feat._ivy-lantern_song_(harbour_54_remix)")]
    [InlineData("Moonward_Glass Harbour (Deluxe)_01_Lantern Song", "Lantern Song")]
    [InlineData("Moonward_Glass Harbour_17_100 Lanterns", "100 Lanterns")]
    [InlineData("03 - Lantern Song [2]", "Lantern Song [2]")]
    [InlineData("03 - Lantern Song [2] Harbour Mix", "Lantern Song [2] Harbour Mix")]
    [InlineData("Moonward - Night Harbour [01-03] (Reprise) Salt Road", "(Reprise) Salt Road")]
    [InlineData("05 - Moonward - Lantern Song [16-44]", "Lantern Song [16-44]")]
    [InlineData("03 - Lantern Song (Live 12_04_99)", "Lantern Song (Live 12_04_99)")]
    public void the_tagger_reads_the_title_out_of_field_and_index_layouts(string fileName, string expected)
    {
        Assert.Equal(expected, TrackTitleMatcher.TitleFromFilename(fileName));
    }

    [Fact]
    public void numbers_left_over_in_a_file_title_are_fields_not_qualifiers()
    {
        Assert.Equal(new Dictionary<int, int> { [0] = 0 }, TrackTitleMatcher.Match(["1999 2005 15 Lantern Song"], ["Lantern Song"]));
    }

    [Fact]
    public void two_leftover_words_still_keep_the_tagger_from_pairing()
    {
        Assert.Empty(TrackTitleMatcher.Match(["Lantern Song Harbour Dub"], ["Lantern Song"]));
    }

    [Theory]
    [InlineData("Lantern Song (Take 2)", "Lantern Song", false)]
    [InlineData("Lantern Song Part 2", "Lantern Song", false)]
    [InlineData("Lantern Song, Pt. 2", "Lantern Song, Part II", true)]
    [InlineData("Lantern Song (Remastered 2011)", "Lantern Song", true)]
    [InlineData("Take Me Harbour (Take 2)", "Take Me Harbour", false)]
    [InlineData("No Lanterns (Take 2)", "No Lanterns", false)]
    [InlineData("Lantern Song, Part Three", "Lantern Song, Part Six", false)]
    [InlineData("Lantern Song, Part 2", "Lantern Song, Part Two", true)]
    [InlineData("Lantern Song Pt. 1", "Lantern Song, Set 1", true)]
    [InlineData("Lantern Song, Part 11", "Lantern Song, Part XI", true)]
    [InlineData("Lantern Song, Part 11", "Lantern Song, Part Eleven", true)]
    [InlineData("Lantern Song, Part Eleven", "Lantern Song, Part Twelve", false)]
    [InlineData("One Lantern, Part One", "One Lantern, Part Two", false)]
    [InlineData("Two Lanterns (Take 2)", "Two Lanterns", false)]
    [InlineData("Lantern Song (Set 2)", "Lantern Song, Set II", true)]
    [InlineData("Lantern Song (Set 2)", "Lantern Song, Set I", false)]
    [InlineData("Lantern Song pt. 1", "Lantern Song 1", true)]
    public void a_numbered_take_or_part_is_another_track_unless_the_wanted_title_is_numbered(string local, string wanted, bool pairs)
    {
        Assert.Equal(pairs, TrackTitleMatcher.Match([local], [wanted]).Count == 1);
    }

    [Fact]
    public void a_dollar_amount_keeps_its_number_for_the_tagger()
    {
        Assert.Empty(TrackTitleMatcher.Match(["$20 Lantern Song"], ["$100 Lantern Song"]));
    }

    // On Japanese, Thai or Indic text a mark changes the word.
    [Theory]
    [InlineData("ガラスの歌", "カラスの歌")]
    [InlineData("काला", "कल")]
    [InlineData("ठिप", "ठूप")]
    [InlineData("मूझ", "झिम")]
    [InlineData("กิบ", "กูบ")]
    [InlineData("Лумой", "Лумои")]
    [InlineData("Зорїн", "Зорін")]
    [InlineData("Краўн", "Краун")]
    [InlineData("ދިވެހި", "ދވހ")]
    [InlineData("ހި", "ހ")]
    public void marks_outside_latin_letters_keep_two_words_apart(string a, string b)
    {
        Assert.NotEqual(ComparableText.Of(a), ComparableText.Of(b));
        Assert.Empty(TrackTitleMatcher.Match([a], [b]));
        Assert.False(StoreReleaseVerifier.SameAlbumTitle(a, b));
        Assert.NotEqual(StoreQueryCleaner.MatchKey(a), StoreQueryCleaner.MatchKey(b));
    }

    [Fact]
    public void a_mark_that_spells_the_word_stays_inside_it()
    {
        Assert.Equal("ठिप", ComparableText.Of("ठिप"));
    }

    // Greek capitals drop the tonos, Arabic and Hebrew usually leave vowel marks out, and IPA letters carry tone marks.
    [Theory]
    [InlineData("Λιμάνι Φανάρι", "ΛΙΜΑΝΙ ΦΑΝΑΡΙ")]
    [InlineData("زَقَم", "زقم")]
    [InlineData("גָּלֶב", "גלב")]
    [InlineData("Mɔ́lɛ́ Sɔ́ŋ", "Mɔlɛ Sɔŋ")]
    [InlineData("Сёмга", "Семга")]
    [InlineData("Λιμάνις", "ΛΙΜΑΝΙΣ")]
    public void optional_marks_fold_on_every_other_script(string marked, string plain)
    {
        Assert.Equal(ComparableText.Of(plain), ComparableText.Of(marked));
        Assert.Single(TrackTitleMatcher.Match([plain], [marked]));
    }

    [Fact]
    public void a_decomposed_kana_mark_composes_like_the_title()
    {
        Assert.Equal(ComparableText.Of("ガラスの歌"), ComparableText.Of("カ\u3099ラスの歌"));
    }

    [Theory]
    [InlineData("01 - Glass Harbour", false)]
    [InlineData("01 - U.F.O.", true)]
    public void a_single_whose_initialism_title_joins_under_four_letters_is_still_checked(string fileTitle, bool matched)
    {
        const string folder = @"Music\Moonward\2001 - U.F.O";
        List<SlskdFileData> files =
        [
            new($@"{folder}\{fileTitle}.flac", null, 16, 30_000_000, 200, ".flac", 44100, 0, false),
            new($@"{folder}\02 - Salt Road.flac", null, 16, 30_000_000, 200, ".flac", 44100, 0, false),
        ];
        AlbumData album = Parser.CreateAlbumData(
            files.GroupBy(f => SlskdTextProcessor.GetMergedDirectoryKey(f.Filename)).Single(),
            new SlskdSearchData("Moonward", "U.F.O.", false, false, 1, null, TrackCount: 1, Tracks: ["U.F.O."], AlbumType: "Single"),
            new SlskdFolderData(folder, "Moonward", "U.F.O", "2001", "peer", true, 1_000_000, 0, [], 0, 0, files.Count, [], 0),
            new SlskdSettings(),
            expectedTrackCount: 1);

        Assert.Equal(matched, album.MatchedSearchCriteria);
    }

    [Theory]
    [InlineData("Lantern Song (K?D remix)", "Lantern Song (KD Remix)", false)]
    [InlineData("Lantern Song (Ki:Theory remix)", "Lantern Song KiTheory remix", false)]
    [InlineData("Lantern Song (Moonward remix)", "Lantern Song (Harbour remix)", true)]
    [InlineData("Lantern Song (Moonward remix)", "Lantern Song (Moonward's Late Night remix)", false)]
    [InlineData("Lantern Song (Ivy remix)", "Lantern Song (Ivy's Late Night remix)", false)]
    [InlineData("Lantern Song (Corvin remix)", "Lantern Song (Corvina Brothers remix)", true)]
    [InlineData("Lantern Song (DJ Sun remix)", "Lantern Song (DJ Sunburst Harbour remix)", true)]
    [InlineData("Lantern Song (Dan remix)", "Lantern Song (Jordan Harbour remix)", true)]
    [InlineData("Lantern Song (Dan remix)", "Lantern Song (Danny Harbour remix)", true)]
    [InlineData("Lantern Song (Harbour remix)", "Lantern Song (Moonharbour Late Night remix)", true)]
    public void a_stylised_remixer_name_split_differently_is_the_same_remixer(string wanted, string candidate, bool conflict)
    {
        Assert.Equal(conflict, VariantQualifiers.RemixSignaturesConflict(wanted, candidate));
    }

    [Theory]
    [InlineData("Hi‐Fi Night", "Hi Fi Night")]
    [InlineData("Ünder Störm", "Under Storm")]
    [InlineData("The Glass Harbour", "Glass Harbour")]
    public void store_titles_compare_through_the_same_form(string a, string b)
    {
        Assert.Equal(StoreReleaseVerifier.Normalize(a), StoreReleaseVerifier.Normalize(b));
    }

    [Theory]
    [InlineData("V/A")]
    [InlineData("V.A.")]
    [InlineData("VA")]
    [InlineData("Various Artists")]
    [InlineData("VARIOUS ARTISTS")]
    [InlineData("Various-Artists")]
    [InlineData(" Various Artists ")]
    public void various_artists_is_known_however_it_is_written(string artist)
    {
        Assert.True(StoreReleaseVerifier.IsVariousArtists(artist));
    }

    // One check for the store verifier, the Qobuz result filter and the pre-import tagger's artist-tag guard.
    [Theory]
    [InlineData("Various")]
    [InlineData("Vanessa")]
    [InlineData("Soundtrack")]
    [InlineData("")]
    [InlineData(null)]
    public void other_names_are_not_various_artists(string? artist)
    {
        Assert.False(StoreReleaseVerifier.IsVariousArtists(artist));
    }

    private static bool FolderMatches(string folder, string artist, string album)
    {
        List<SlskdFileData> files = [new($@"{folder}\01 Track.flac", null, 16, 30_000_000, 212, ".flac", 44100, 0, false)];
        AlbumData data = Parser.CreateAlbumData(
            files.GroupBy(f => SlskdTextProcessor.GetMergedDirectoryKey(f.Filename)).Single(),
            new SlskdSearchData(artist, album, false, false, 1, null),
            new SlskdFolderData(folder, artist, album, "", "peer", true, 1_000_000, 0, [], 0, 0, 0, [], 0),
            new SlskdSettings(),
            expectedTrackCount: 1);
        return data.MatchedSearchCriteria;
    }
}
