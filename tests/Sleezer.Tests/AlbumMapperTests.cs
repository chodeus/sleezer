using NzbDrone.Core.Music;
using NzbDrone.Plugin.Sleezer.Metadata.Proxy.MetadataProvider;
using Xunit;

namespace Sleezer.Tests;

public class AlbumMapperTests
{
    [Theory]
    [InlineData("Ghost Stories")]
    [InlineData("Lost Highway")]
    [InlineData("Olive")]
    [InlineData("Democracy")]
    [InlineData("Spreading the Disease")]
    public void A_title_that_only_contains_a_keyword_inside_a_word_gets_no_secondary_type(string title)
    {
        Assert.Empty(AlbumMapper.DetermineSecondaryTypesFromTitle(title));
    }

    [Theory]
    [InlineData("Back in Black (Remastered)")]
    [InlineData("Rumours (Deluxe Edition)")]
    [InlineData("Ten (Expanded Edition)")]
    public void A_remaster_or_new_edition_is_not_a_remix(string title)
    {
        Assert.Empty(AlbumMapper.DetermineSecondaryTypesFromTitle(title));
    }

    [Theory]
    [InlineData("Live at Wembley", "Live")]
    [InlineData("The Very Best of Somebody", "Compilation")]
    [InlineData("Greatest Hits", "Compilation")]
    [InlineData("Film (OST)", "Soundtrack")]
    [InlineData("The Remixes", "Remix")]
    [InlineData("Early Demos", "Demo")]
    public void A_keyword_as_a_whole_word_still_names_the_type(string title, string type)
    {
        Assert.Contains(AlbumMapper.DetermineSecondaryTypesFromTitle(title), t => t.Name == type);
    }
}
