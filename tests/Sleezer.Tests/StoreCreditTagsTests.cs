using NzbDrone.Plugin.Sleezer.Core.Utilities;
using Xunit;

namespace Sleezer.Tests;

public class StoreCreditTagsTests
{
    [Fact]
    public void The_first_non_blank_copyright_wins_and_composers_are_cleaned()
    {
        var tag = new TagLib.Id3v2.Tag();

        StoreCreditTags.Apply(tag, " USABC2600001 ", [" ", "(P) 2026 Track Label", "(P) 2026 Album Label"], ["Ann Writer", " ann writer ", null, "", "Bo Writer"]);

        Assert.Equal("USABC2600001", tag.ISRC);
        Assert.Equal("(P) 2026 Track Label", tag.Copyright);
        Assert.Equal(new[] { "Ann Writer", "Bo Writer" }, tag.Composers);
    }

    [Fact]
    public void A_value_the_store_lacks_leaves_the_tag_alone()
    {
        var tag = new TagLib.Id3v2.Tag { ISRC = "KEEP00000001", Copyright = "kept", Composers = ["Kept Composer"] };

        StoreCreditTags.Apply(tag, null, [null, " "], [null]);

        Assert.Equal("KEEP00000001", tag.ISRC);
        Assert.Equal("kept", tag.Copyright);
        Assert.Equal(new[] { "Kept Composer" }, tag.Composers);
    }
}
