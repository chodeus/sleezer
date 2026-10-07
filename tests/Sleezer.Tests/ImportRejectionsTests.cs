using NzbDrone.Plugin.Sleezer.Notifications.QueueCleaner;
using Xunit;

namespace Sleezer.Tests;

// Messages as Lidarr's import specifications and ImportDecisionMaker word them.
public class ImportRejectionsTests
{
    [Theory]
    [InlineData("Has missing tracks", true, false)]
    [InlineData("Has fewer tracks than existing release", true, false)]
    [InlineData("Album match is not close enough: 71.2% vs 80% [album, tracks]", false, true)]
    [InlineData("Worst track match: 45.0% vs 60% [track_title]", false, true)]
    [InlineData("Has unmatched tracks", false, true)]
    [InlineData("Couldn't find similar album for Artist - Album", false, true)]
    [InlineData("Not an upgrade for existing album file(s)", false, false)]
    public void classifies_each_rejection(string message, bool missingTracks, bool poorMatch)
    {
        Assert.Equal((missingTracks, poorMatch), ImportRejections.Classify([message]));
    }

    [Fact]
    public void a_download_can_fail_both_ways()
    {
        Assert.Equal((true, true), ImportRejections.Classify(["Has missing tracks", "Has unmatched tracks"]));
    }
}
