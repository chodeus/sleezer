using NzbDrone.Plugin.Sleezer.Notifications.Queue;
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

    [Theory]
    [InlineData(ImportCleaningOptions.WhenMissingTracks, "Has missing tracks", true)]
    [InlineData(ImportCleaningOptions.WhenMissingTracks, "Has unmatched tracks", false)]
    [InlineData(ImportCleaningOptions.WhenAlbumInfoIncomplete, "Has unmatched tracks", true)]
    [InlineData(ImportCleaningOptions.WhenAlbumInfoIncomplete, "Has fewer tracks than existing release", false)]
    [InlineData(ImportCleaningOptions.Always, "Has missing tracks", true)]
    [InlineData(ImportCleaningOptions.Always, "Worst track match: 45.0% vs 60% [track_title]", true)]
    [InlineData(ImportCleaningOptions.Always, "Not an upgrade for existing album file(s)", false)]
    [InlineData(ImportCleaningOptions.Disabled, "Has missing tracks", false)]
    public void each_option_cleans_only_its_own_rejections(ImportCleaningOptions option, string message, bool clean)
    {
        Assert.Equal(clean, ImportRejections.ShouldClean(option, [message]));
    }

    [Theory]
    [InlineData(ImportCleaningOptions.WhenMissingTracks, true)]
    [InlineData(ImportCleaningOptions.WhenAlbumInfoIncomplete, true)]
    [InlineData(ImportCleaningOptions.Always, true)]
    [InlineData(ImportCleaningOptions.Disabled, false)]
    public void a_download_failing_both_ways_matches_either_option(ImportCleaningOptions option, bool clean)
    {
        Assert.Equal(clean, ImportRejections.ShouldClean(option, ["Has fewer tracks than existing release", "Has unmatched tracks"]));
    }
}
