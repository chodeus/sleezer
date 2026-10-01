using NzbDrone.Plugin.Sleezer.Core.Utilities;
using Xunit;

namespace Sleezer.Tests;

// Live 2026-10-01: DJ mixes and soundtracks from Deezer and Qobuz failed import on every track title.
public class SharedTrackQualifierTests
{
    private static readonly string[] Target = ["One", "Two", "First Track", "Second Track (Remixer Name remix)"];

    [Fact]
    public void A_bracket_the_target_tracklist_carries_is_kept()
    {
        string[] titles = ["First Track (Artist's Version)", "Second Track (Artist's Version)"];

        Assert.Equal(titles, SharedTrackQualifier.Drop(titles, ["First Track (Artist's Version)", "Second Track (Artist's Version)"]));
    }

    [Fact]
    public void An_unknown_target_tracklist_changes_nothing()
    {
        string[] titles = ["One (Mixed)", "Two (Mixed)"];

        Assert.Equal(titles, SharedTrackQualifier.Drop(titles, []));
    }

    [Fact]
    public void A_bracket_every_track_carries_is_dropped_wherever_it_sits()
    {
        Assert.Equal(
            ["First Track", "Second Track (Remixer Name Remix)"],
            SharedTrackQualifier.Drop(["First Track (Mixed)", "Second Track (Mixed) (Remixer Name Remix)"], Target));
    }

    [Theory]
    [InlineData("Mix Cut")]
    [InlineData("Original Soundtrack")]
    [InlineData("Artist Name Presents Other Artist")]
    public void Store_wording_shared_by_every_track_is_dropped(string shared)
    {
        Assert.Equal(["One", "Two"], SharedTrackQualifier.Drop([$"One ({shared})", $"Two [{shared}]"], Target));
    }

    [Theory]
    [InlineData("Live")]
    [InlineData("Instrumental")]
    [InlineData("Acoustic")]
    [InlineData("Radio Edit")]
    public void A_shared_variant_is_kept(string shared)
    {
        string[] titles = [$"One ({shared})", $"Two ({shared})"];

        Assert.Equal(titles, SharedTrackQualifier.Drop(titles, Target));
    }

    [Fact]
    public void A_bracket_only_some_tracks_carry_is_kept()
    {
        string[] titles = ["One (Mixed)", "Two"];

        Assert.Equal(titles, SharedTrackQualifier.Drop(titles, Target));
    }

    [Fact]
    public void A_single_track_is_left_alone()
    {
        string[] titles = ["One (Original Soundtrack)"];

        Assert.Equal(titles, SharedTrackQualifier.Drop(titles, Target));
    }

    // The tagger judges on every file's original title, then strips only the files it left alone.
    [Fact]
    public void A_bracket_one_original_file_lacks_is_not_removable()
    {
        Assert.Empty(SharedTrackQualifier.Removable(["One (Mixed)", "Two (Mixed)", "Three"], Target));
    }

    [Fact]
    public void Without_strips_only_the_removable_bracket()
    {
        var removable = SharedTrackQualifier.Removable(["One (Mixed)", "Two (Mixed) (Remixer Name Remix)"], Target);

        Assert.Equal("Two (Remixer Name Remix)", SharedTrackQualifier.Without("Two (Mixed) (Remixer Name Remix)", removable));
    }
}
