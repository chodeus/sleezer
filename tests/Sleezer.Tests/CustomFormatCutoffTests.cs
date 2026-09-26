using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Profiles;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Plugin.Sleezer.Metadata.ScheduledTasks.SearchSniper;
using Xunit;

namespace Sleezer.Tests;

public class CustomFormatCutoffTests
{
    private static readonly CustomFormat Web = new("WEB") { Id = 1 };
    private static readonly CustomFormat Cd = new("CD") { Id = 2 };
    private static readonly CustomFormat Vinyl = new("Vinyl") { Id = 3 };

    private static QualityProfile Profile(int id, int cutoff, bool upgradeAllowed = true, params (CustomFormat Format, int Score)[] scores) =>
        new()
        {
            Id = id,
            UpgradeAllowed = upgradeAllowed,
            CutoffFormatScore = cutoff,
            FormatItems = [.. scores.Select(s => new ProfileFormatItem { Format = s.Format, Score = s.Score })]
        };

    private static readonly QualityProfile HighQuality = Profile(4, 50, true, (Web, 50), (Cd, 3), (Vinyl, -10));

    [Fact]
    public void A_file_scoring_below_the_cutoff_is_unmet()
    {
        Assert.True(CustomFormatCutoff.IsUnmet(HighQuality, [Cd]));
    }

    [Fact]
    public void A_file_scoring_the_cutoff_is_met()
    {
        Assert.False(CustomFormatCutoff.IsUnmet(HighQuality, [Web]));
    }

    [Fact]
    public void A_file_with_no_formats_scores_zero()
    {
        Assert.True(CustomFormatCutoff.IsUnmet(HighQuality, []));
        Assert.False(CustomFormatCutoff.IsUnmet(Profile(1, 0), []));
    }

    [Fact]
    public void A_profile_that_allows_no_upgrades_is_skipped()
    {
        QualityProfile frozen = Profile(1, 50, false, (Web, 50));

        Assert.Empty(CustomFormatCutoff.Profiles([frozen]));
    }

    // Every file scores at least 0 here, so none can be below a cutoff of 0.
    [Fact]
    public void A_profile_no_file_can_fall_below_is_skipped()
    {
        QualityProfile unreachable = Profile(1, 0, true, (Web, 50), (Cd, 3));

        Assert.Empty(CustomFormatCutoff.Profiles([unreachable]));
    }

    [Fact]
    public void A_negative_score_makes_a_zero_cutoff_reachable()
    {
        QualityProfile penalised = Profile(1, 0, true, (Vinyl, -1));

        Assert.Equal([penalised], CustomFormatCutoff.Profiles([penalised]));
    }

    [Fact]
    public void Only_profiles_with_a_reachable_cutoff_are_kept()
    {
        QualityProfile unreachable = Profile(2, 0, true, (Web, 50));

        Assert.Equal([HighQuality], CustomFormatCutoff.Profiles([unreachable, HighQuality]));
    }
}
