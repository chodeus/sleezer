using NzbDrone.Core.Indexers.Qobuz;
using Xunit;

namespace Sleezer.Tests;

// Qobuz keeps version words in a field its search does not index, so the cleaned query is what
// finds "Words Remixes"; the gate only skips it once an earlier query has found the album.
public class QobuzQueryPlanTests
{
    [Fact]
    public void Raw_query_runs_first_and_is_never_gated()
    {
        List<QobuzQuery> plan = QobuzQueryPlan.Build("Artist", "Words Remixes");

        Assert.Equal(new QobuzQuery(1, "Artist Words Remixes", false), plan[0]);
    }

    [Fact]
    public void Cleaned_query_shares_tier_one_but_is_gated()
    {
        List<QobuzQuery> plan = QobuzQueryPlan.Build("Artist", "Words Remixes");

        Assert.Contains(new QobuzQuery(1, "Artist Words", true), plan);
    }

    [Fact]
    public void A_cleaned_query_identical_to_the_raw_one_is_not_repeated()
    {
        List<QobuzQuery> plan = QobuzQueryPlan.Build("Artist", "Plain Title");

        Assert.Single(plan);
    }

    [Fact]
    public void Trailing_subtitle_query_shares_tier_one_but_is_gated()
    {
        List<QobuzQuery> plan = QobuzQueryPlan.Build("Artist", "Imagine: The Evolution Documentary");

        Assert.Contains(new QobuzQuery(1, "Artist Imagine", true), plan);
        Assert.DoesNotContain(plan, q => q.Tier != 1);
    }

    [Fact]
    public void Split_release_halves_share_tier_one_but_are_gated()
    {
        List<QobuzQuery> plan = QobuzQueryPlan.Build("Artist", "Alpha / Beta");

        Assert.Contains(new QobuzQuery(1, "Artist Alpha", true), plan);
        Assert.Contains(new QobuzQuery(1, "Artist Beta", true), plan);
    }

    [Fact]
    public void Dotted_acronyms_are_collapsed_in_the_cleaned_query()
    {
        List<QobuzQuery> plan = QobuzQueryPlan.Build("S.H.I.E.L.D.", "Agents");

        Assert.Contains(new QobuzQuery(1, "SHIELD Agents", true), plan);
    }

    // The artist goes through GetQueryTitle as CleanArtistQuery did, so a leading "The" still drops.
    [Fact]
    public void The_artist_is_normalised_like_lidarrs_clean_artist_query()
    {
        Assert.Contains(new QobuzQuery(1, "Killers Hot Fuss", true), QobuzQueryPlan.Build("The Killers", "Hot Fuss"));
    }

    [Fact]
    public void An_artist_only_search_has_just_the_raw_query()
    {
        Assert.Single(QobuzQueryPlan.Build("Artist", ""));
    }
}
