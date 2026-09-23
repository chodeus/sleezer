using NzbDrone.Plugin.Sleezer.Core.Utilities;
using Xunit;

namespace Sleezer.Tests;

public class StoreQueryCleanerTests
{
    [Theory]
    [InlineData("Batman: Original Motion Picture Score", "Batman")]
    [InlineData("The Hack: Original Television Soundtrack", "The Hack")]
    [InlineData("Album (Deluxe Edition)", "Album")]
    [InlineData("Apollo (The Remixes)", "Apollo")]
    [InlineData("Discovery [Remastered]", "Discovery")]
    [InlineData("Title - 10th Anniversary Edition", "Title")]
    [InlineData("Plain Title", "Plain Title")]
    [InlineData("A / B", "A / B")]
    public void StripQualifiers_reduces_to_the_core_title(string title, string expected)
    {
        Assert.Equal(expected, StoreQueryCleaner.StripQualifiers(title));
    }

    [Theory]
    [InlineData("Deluxe")]     // an album literally named after a qualifier keeps its name
    [InlineData("(Remixes)")]
    public void StripQualifiers_keeps_the_original_when_nothing_would_remain(string title)
    {
        Assert.Equal(title, StoreQueryCleaner.StripQualifiers(title));
    }

    // Deezer's artist:"…" / album:"…" syntax has no escape, so a literal quote must never reach it.
    [Theory]
    [InlineData("Songs from \"Frozen\"", "Songs from Frozen")]
    [InlineData("\"Weird Al\" Yankovic", "Weird Al Yankovic")]
    [InlineData("No quotes", "No quotes")]
    [InlineData("", "")]
    public void WithoutQuotes_removes_literal_double_quotes(string value, string expected)
    {
        Assert.Equal(expected, StoreQueryCleaner.WithoutQuotes(value));
    }

    [Theory]
    [InlineData("Rock+Roll's  Here", "Rock Roll s Here")]
    [InlineData("  plain  ", "plain")]
    [InlineData("", "")]
    public void CleanForTokenSearch_normalises_plus_apostrophes_and_spacing(string query, string expected)
    {
        Assert.Equal(expected, StoreQueryCleaner.CleanForTokenSearch(query));
    }

    // Query side only: Qobuz keeps these words in a `version` field its search never indexes.
    [Theory]
    [InlineData("Turn It Up (Remixes)", "Turn It Up")]
    [InlineData("Words Remixes", "Words")]
    [InlineData("Guilty as Charged: The Remixes", "Guilty as Charged")]
    [InlineData("Album: Live at Wembley", "Album")]
    [InlineData("Album Live at Wembley", "Album")]
    [InlineData("Songs to Love and Live", "Songs to Love and Live")]
    [InlineData("Live at Wembley", "Live at Wembley")]
    [InlineData("Plain Title", "Plain Title")]
    [InlineData("Remixes", "Remixes")]
    public void StripForSearch_drops_the_version_words_a_store_search_cannot_see(string title, string expected)
    {
        Assert.Equal(expected, StoreQueryCleaner.StripForSearch(title));
    }

    // StoreReleaseVerifier compares titles with StripQualifiers; a live or remix album must not verify as the studio one.
    [Theory]
    [InlineData("Album: Live at Wembley")]
    [InlineData("Words Remixes")]
    public void StripQualifiers_keeps_version_words_for_verification(string title)
    {
        Assert.Equal(title, StoreQueryCleaner.StripQualifiers(title));
    }

    [Theory]
    [InlineData("S.H.I.E.L.D.", "SHIELD")]
    [InlineData("Agents of S.H.I.E.L.D. Live", "Agents of SHIELD Live")]
    [InlineData("A.B", "A.B")]
    [InlineData("Plain", "Plain")]
    public void CollapseAcronyms_joins_dotted_letters(string value, string expected)
    {
        Assert.Equal(expected, StoreQueryCleaner.CollapseAcronyms(value));
    }

    [Theory]
    [InlineData("Imagine: The Evolution Documentary", "Imagine")]
    [InlineData("Imagine: The Evolution: Documentary", "Imagine")]
    [InlineData("No subtitle here", "No subtitle here")]
    [InlineData("Ratio 1:2", "Ratio 1:2")]
    public void StripTrailingSubtitle_drops_a_colon_subtitle(string title, string expected)
    {
        Assert.Equal(expected, StoreQueryCleaner.StripTrailingSubtitle(title));
    }

    // Gates the fallback query, so it must keep apart what StripForSearch deliberately folds together.
    [Fact]
    public void MatchKey_keeps_editions_apart_and_ignores_case_accents_and_punctuation()
    {
        Assert.NotEqual(StoreQueryCleaner.MatchKey("Turn It Up"), StoreQueryCleaner.MatchKey("Turn It Up (Remixes)"));
        Assert.Equal(StoreQueryCleaner.MatchKey("Vespertine"), StoreQueryCleaner.MatchKey("vespertine!"));
        Assert.Equal("cafe del mar", StoreQueryCleaner.MatchKey("Café del Mar!"));
        Assert.Null(StoreQueryCleaner.MatchKey("   "));
        Assert.NotEqual(StoreQueryCleaner.MatchKey("Live 東京"), StoreQueryCleaner.MatchKey("Live 大阪"));
    }
}
