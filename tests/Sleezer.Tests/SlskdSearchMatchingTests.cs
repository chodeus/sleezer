using NLog;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Core.Model;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek;
using NzbDrone.Plugin.Sleezer.Indexers.Soulseek.Search.Transformers;
using Xunit;

namespace Sleezer.Tests;

// Covers the slskd search/matching fixes from the 2026-07 audit: query
// normalization that used to fuse words ("G‐Eazy" → "GEazy" → zero results),
// disc-subfolder grouping, folder-name parsing splits, and multi-disc
// download-item path handling.
public class QueryNormalizerTests
{
    [Theory]
    [InlineData("G‐Eazy", "G-Eazy")]                    // U+2010 HYPHEN (MusicBrainz) → ASCII, not deleted
    [InlineData("Sigur Rós", "Sigur Ros")]
    [InlineData("Ágætis byrjun", "Agaetis byrjun")] // ligature æ folds to ae
    [InlineData("Don't", "Dont")]                             // apostrophes are elision, not separators
    [InlineData("Superstylin’", "Superstylin")]
    [InlineData("AC/DC", "AC DC")]
    [InlineData("Weezer+Blue Album", "Weezer Blue Album")]
    [InlineData("Him & I", "Him & I")]
    public void NormalizeText_produces_peer_matchable_terms(string input, string expected)
    {
        Assert.Equal(expected, QueryNormalizer.NormalizeText(input));
    }
}

public class QueryBuilderTests
{
    [Fact]
    public void ConvertRomanNumeral_does_not_convert_the_pronoun_I()
    {
        Assert.Null(QueryBuilder.ConvertRomanNumeral("Him & I"));
    }

    [Fact]
    public void ConvertRomanNumeral_still_converts_real_numerals()
    {
        Assert.Equal("Led Zeppelin 4", QueryBuilder.ConvertRomanNumeral("Led Zeppelin IV"));
    }

    [Theory]
    [InlineData("Superstylin (remixes)", "Superstylin")]
    [InlineData("OK Computer (Deluxe Edition)", "OK Computer")]
    [InlineData("Lateralus", null)]                           // nothing to strip
    public void StripEditionSuffixes_removes_bracketed_tails(string input, string? expected)
    {
        Assert.Equal(expected, QueryBuilder.StripEditionSuffixes(input));
    }
}

public class SlskdTextProcessorTests
{
    [Theory]
    [InlineData(@"@@x\Artist\Album\CD1\01.flac", @"@@x\Artist\Album")]
    [InlineData(@"@@x\Artist\Album\Disc 2\01.flac", @"@@x\Artist\Album")]
    [InlineData(@"@@x\Artist\Album\01.flac", @"@@x\Artist\Album")]
    [InlineData(@"@@x\Artist\CD Collection\01.flac", @"@@x\Artist\CD Collection")] // not a disc leaf
    public void GetMergedDirectoryKey_folds_disc_subfolders(string filename, string expected)
    {
        Assert.Equal(expected, SlskdTextProcessor.GetMergedDirectoryKey(filename));
    }

    [Fact]
    public void GetDirectoryFromFilename_handles_posix_separators()
    {
        Assert.Equal("music/Artist/Album", SlskdTextProcessor.GetDirectoryFromFilename("music/Artist/Album/01.flac"));
    }
}

public class SlskdItemsParserTests
{
    private static readonly SlskdItemsParser Parser = new(LogManager.GetCurrentClassLogger());

    [Fact]
    public void NormalizeString_treats_backslash_as_separator()
    {
        string normalized = SlskdItemsParser.NormalizeString(@"@@va15b\Music\Taylor Swift\1989 (2014) [FLAC]");
        Assert.Contains("taylor swift 1989", normalized);
    }

    [Fact]
    public void NormalizeString_never_returns_empty_for_stopword_names()
    {
        Assert.Equal("the the", SlskdItemsParser.NormalizeString("The The"));
    }

    [Fact]
    public void ParseFolderName_keeps_hyphenated_artist_names_intact()
    {
        SlskdFolderData data = Parser.ParseFolderName(@"@@u\Jay-Z - The Blueprint (2001)");
        Assert.Equal("Jay-Z", data.Artist);
        Assert.Equal("The Blueprint", data.Album);
        Assert.Equal("2001", data.Year);
    }

    [Fact]
    public void ParseFolderName_parses_year_led_discography_folders()
    {
        SlskdFolderData data = Parser.ParseFolderName(@"@@u\Nirvana\1993 - In Utero");
        Assert.Equal("Nirvana", data.Artist);
        Assert.Equal("In Utero", data.Album);
        Assert.Equal("1993", data.Year);
    }

    [Fact]
    public void ParseFolderName_parses_year_artist_album_folders()
    {
        SlskdFolderData data = Parser.ParseFolderName(@"@@u\1969 - The Stooges - The Stooges");
        Assert.Equal("The Stooges", data.Artist);
        Assert.Equal("The Stooges", data.Album);
        Assert.Equal("1969", data.Year);
    }

    [Fact]
    public void ParseFolderName_rejects_share_roots_as_artist()
    {
        SlskdFolderData data = Parser.ParseFolderName(@"@@abc123\Nevermind (1991)");
        Assert.NotEqual("@@abc123", data.Artist);
    }
}

public class SlskdDownloadItemMultiDiscTests
{
    private static SlskdDownloadItem NewItem(params string[] filenames)
    {
        string source = "[" + string.Join(",", filenames.Select(f =>
            $"{{\"Filename\":{System.Text.Json.JsonSerializer.Serialize(f)},\"Size\":1000}}")) + "]";
        return new SlskdDownloadItem(new ReleaseInfo { Source = source, Title = "t", DownloadUrl = "u" });
    }

    [Fact]
    public void Single_directory_items_are_not_multi_directory()
    {
        SlskdDownloadItem item = NewItem(@"@@x\Artist\Album\01.flac", @"@@x\Artist\Album\02.flac");
        Assert.False(item.IsMultiDirectory);
    }

    [Fact]
    public void Disc_subfolders_make_a_multi_directory_item_with_album_output_folder()
    {
        SlskdDownloadItem item = NewItem(@"@@x\Artist\Album\CD1\01.flac", @"@@x\Artist\Album\CD2\01.flac");
        Assert.True(item.IsMultiDirectory);
        Assert.Equal("Album", item.LocalAlbumFolderName());
        Assert.Equal(new HashSet<string> { "CD1", "CD2" }, new HashSet<string>(item.RemoteDirectoryLeaves()));
    }

    [Fact]
    public void Ownership_lookup_matches_enqueued_files()
    {
        SlskdDownloadItem item = NewItem(@"@@x\Artist\Album\CD1\01.flac", @"@@x\Artist\Album\CD2\01.flac");
        Assert.True(item.OwnsFile(@"@@x\Artist\Album\CD2\01.flac"));
        Assert.False(item.OwnsFile(@"@@x\Artist\Other\01.flac"));
    }
}

public class ShareInfoTests
{
    [Fact]
    public void ToShareInfo_carries_priority_as_seeders_and_match_flag()
    {
        AlbumData albumData = new("Slskd", "SoulseekDownloadProtocol")
        {
            ArtistName = "Artist",
            AlbumName = "Album",
            AlbumId = "/api/v0/transfers/downloads/user",
            Priotity = 4321,
            MatchedSearchCriteria = true,
        };

        ShareInfo release = albumData.ToShareInfo();
        Assert.Equal(4321, release.Seeders);
        Assert.True(release.MatchedSearchCriteria);
    }
}

// Single/EP-target handling: pluck only the wanted track(s) from an album share
// (don't haul the whole album), reject a source that holds none of them, and
// prefer a coherent source over a partial one for a multi-track single/EP.
public class SlskdSingleSourceTests
{
    private static readonly SlskdItemsParser Parser = new(LogManager.GetCurrentClassLogger());

    private static SlskdFileData Audio(string filename) => new(
        Filename: filename, BitRate: 900, BitDepth: 16, Size: 1000, Length: 200,
        Extension: "flac", SampleRate: 44100, Code: 1, IsLocked: false);

    private static AlbumData Build(string dirKey, string[] files, string artist, string album,
        string[] tracks, int expectedTrackCount, SlskdSettings? settings = null)
    {
        IGrouping<string, SlskdFileData> group = files.Select(Audio).GroupBy(_ => dirKey).Single();
        SlskdFolderData folder = Parser.ParseFolderName(dirKey) with
        {
            Username = "user",
            HasFreeUploadSlot = true,
            FileCount = files.Length
        };
        SlskdSearchData search = new(artist, album, false, false, 1, null,
            TrackCount: expectedTrackCount, Tracks: tracks.ToList());
        return Parser.CreateAlbumData("search1", group, search, folder, settings, expectedTrackCount);
    }

    private static int FlacCount(string customString) => customString.Split(".flac").Length - 1;

    [Fact]
    public void Single_target_plucks_only_the_matched_track_from_an_album_source()
    {
        const string dir = @"@@u\Van Halen\5150 (Expanded Edition)";
        AlbumData a = Build(dir,
            new[]
            {
                dir + @"\01 - Good Enough.flac",
                dir + @"\04 - Dreams.flac",
                dir + @"\05 - Summer Nights.flac",
                dir + @"\06 - Best of Both Worlds.flac",
            },
            artist: "Van Halen", album: "Dreams", tracks: new[] { "Dreams" }, expectedTrackCount: 1);

        Assert.True(a.MatchedSearchCriteria);
        Assert.Equal(1, FlacCount(a.CustomString));           // only the wanted track downloads
        Assert.Contains("Dreams", a.CustomString);
        Assert.DoesNotContain("Best of Both Worlds", a.CustomString);
    }

    [Fact]
    public void Single_target_source_that_matches_by_name_but_holds_no_wanted_track_is_rejected()
    {
        // Folder name matches the single (passes the album-name gate) but no file
        // title-matches the wanted track — rejected even at equal size.
        const string dir = @"@@u\Control Alt Delete\Blackout";
        AlbumData a = Build(dir,
            new[] { dir + @"\01 - Intro Theme.flac" },
            artist: "Control Alt Delete", album: "Blackout", tracks: new[] { "Blackout" }, expectedTrackCount: 1);

        Assert.False(a.MatchedSearchCriteria);
    }

    [Fact]
    public void Multitrack_single_with_overlapping_titles_needs_a_distinct_file_per_title()
    {
        // "Falling" is a substring of "Falling Slowly": a lone "Falling Slowly" file
        // must not satisfy both titles and pass as a complete source.
        string[] tracks = { "Falling", "Falling Slowly" };
        SlskdSettings strict = new() { RequireCoherentSingleSource = true };

        const string dir = @"@@u\Artist\Comp";
        AlbumData a = Build(dir,
            new[] { dir + @"\01 - Falling Slowly.flac", dir + @"\02 - Other Song.flac" },
            artist: "Artist", album: "Falling", tracks: tracks, expectedTrackCount: 2, settings: strict);

        Assert.False(a.MatchedSearchCriteria);   // only 1 of 2 titles has a distinct file → partial → rejected
    }

    [Fact]
    public void Multitrack_single_prefers_a_coherent_source_over_a_partial_one()
    {
        string[] tracks = { "Reborn", "God In You", "I Want My Freedom" };

        const string coh = @"@@u\Tinlicker\Reborn";
        AlbumData coherent = Build(coh,
            new[] { coh + @"\01 - Reborn.flac", coh + @"\02 - God In You.flac", coh + @"\03 - I Want My Freedom.flac" },
            artist: "Tinlicker", album: "Reborn", tracks: tracks, expectedTrackCount: 3);

        const string par = @"@@v\VA\Some Compilation";
        AlbumData partial = Build(par,
            new[] { par + @"\07 - Reborn.flac", par + @"\08 - God In You.flac", par + @"\09 - Unrelated Song.flac" },
            artist: "Tinlicker", album: "Reborn", tracks: tracks, expectedTrackCount: 3);

        Assert.True(coherent.MatchedSearchCriteria);
        Assert.True(partial.MatchedSearchCriteria);           // partial still allowed by default…
        Assert.True(coherent.Priotity > partial.Priotity);    // …but ranked below the coherent source
    }

    [Fact]
    public void Multitrack_single_rejects_a_partial_source_when_require_coherent_is_on()
    {
        string[] tracks = { "Reborn", "God In You", "I Want My Freedom" };
        SlskdSettings strict = new() { RequireCoherentSingleSource = true };

        const string par = @"@@v\VA\Some Compilation";
        AlbumData partial = Build(par,
            new[] { par + @"\07 - Reborn.flac", par + @"\08 - God In You.flac", par + @"\09 - Unrelated Song.flac" },
            artist: "Tinlicker", album: "Reborn", tracks: tracks, expectedTrackCount: 3, settings: strict);

        Assert.False(partial.MatchedSearchCriteria);
    }

    [Fact]
    public void Album_target_larger_than_the_single_ceiling_is_not_narrowed()
    {
        const string dir = @"@@u\Artist\Big Album";
        string[] files =
        {
            dir + @"\01 - Dreams.flac", dir + @"\02 - Panama.flac", dir + @"\03 - Jump.flac",
            dir + @"\04 - Eruption.flac", dir + @"\05 - Fair Warning.flac", dir + @"\06 - Unchained.flac",
            dir + @"\07 - Mean Street.flac",
        };
        AlbumData a = Build(dir, files, artist: "Artist", album: "Big Album",
            tracks: new[] { "Dreams", "Panama", "Jump", "Eruption", "Fair Warning", "Unchained", "Mean Street" },
            expectedTrackCount: 7);                            // > SmallTargetTrackCeiling → whole album kept

        Assert.Equal(7, FlacCount(a.CustomString));
    }

    // Regression: a track whose title normalizes below the 4-char floor
    // ("The End" -> stop-word stripped -> "end") can't be title-matched, so a
    // COMPLETE source must NOT be plucked-incomplete or treated as partial.
    [Fact]
    public void Exact_ep_with_an_unmatchable_track_title_is_not_plucked_incomplete()
    {
        const string dir = @"@@u\Artist\Twilight EP";
        AlbumData a = Build(dir,
            new[] { dir + @"\01 - Dawn.flac", dir + @"\02 - Nightfall.flac", dir + @"\03 - The End.flac" },
            artist: "Artist", album: "Twilight EP",
            tracks: new[] { "Dawn", "Nightfall", "The End" }, expectedTrackCount: 3);

        Assert.True(a.MatchedSearchCriteria);
        Assert.Equal(3, FlacCount(a.CustomString));           // all three kept, incl. the unmatchable track
    }

    [Fact]
    public void Complete_ep_with_an_unmatchable_track_is_not_rejected_under_require_coherent()
    {
        const string dir = @"@@u\Artist\Twilight EP";
        SlskdSettings strict = new() { RequireCoherentSingleSource = true };
        AlbumData a = Build(dir,
            new[] { dir + @"\01 - Dawn.flac", dir + @"\02 - Nightfall.flac", dir + @"\03 - The End.flac" },
            artist: "Artist", album: "Twilight EP",
            tracks: new[] { "Dawn", "Nightfall", "The End" }, expectedTrackCount: 3, settings: strict);

        Assert.True(a.MatchedSearchCriteria);                  // covers every MATCHABLE title → coherent, not rejected
    }
}
