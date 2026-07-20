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
