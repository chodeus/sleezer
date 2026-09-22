using NzbDrone.Core.Indexers.Qobuz;
using Xunit;

namespace Sleezer.Tests;

// Qobuz keeps version words in a field its search does not index, so the cleaned query is what
// finds "Words Remixes"; the gate only skips it once the raw query has answered.
public class QobuzQueryPlanTests
{
    [Fact]
    public void Raw_query_runs_first_and_is_never_gated()
    {
        List<QobuzQuery> plan = QobuzQueryPlan.Build("Artist", "Artist", "Words Remixes");

        Assert.Equal(new QobuzQuery(1, "Artist Words Remixes", false), plan[0]);
    }

    [Fact]
    public void Cleaned_query_shares_tier_one_but_is_gated()
    {
        List<QobuzQuery> plan = QobuzQueryPlan.Build("Artist", "Artist", "Words Remixes");

        Assert.Contains(new QobuzQuery(1, "Artist Words", true), plan);
    }

    [Fact]
    public void A_cleaned_query_identical_to_the_raw_one_is_not_repeated()
    {
        List<QobuzQuery> plan = QobuzQueryPlan.Build("Artist", "Artist", "Plain Title");

        Assert.Single(plan);
    }

    [Fact]
    public void Trailing_subtitle_is_dropped_only_in_tier_two()
    {
        List<QobuzQuery> plan = QobuzQueryPlan.Build("Artist", "Artist", "Imagine: The Evolution Documentary");

        Assert.Contains(new QobuzQuery(2, "Artist Imagine", false), plan);
        Assert.DoesNotContain(plan, q => q.Tier == 1 && q.Query == "Artist Imagine");
    }

    [Fact]
    public void Split_release_halves_share_tier_two()
    {
        List<QobuzQuery> plan = QobuzQueryPlan.Build("Artist", "Artist", "Alpha / Beta");

        Assert.Equal(["Artist Alpha", "Artist Beta"], plan.Where(q => q.Tier == 2).Select(q => q.Query).ToArray());
    }

    [Fact]
    public void Dotted_acronyms_are_collapsed_in_the_cleaned_query()
    {
        List<QobuzQuery> plan = QobuzQueryPlan.Build("S.H.I.E.L.D.", "S.H.I.E.L.D.", "Agents");

        Assert.Contains(new QobuzQuery(1, "SHIELD Agents", true), plan);
    }

    [Fact]
    public void An_artist_only_search_has_just_the_raw_query()
    {
        Assert.Single(QobuzQueryPlan.Build("Artist", "Artist", ""));
    }
}
