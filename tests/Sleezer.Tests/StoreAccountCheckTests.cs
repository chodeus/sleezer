using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Plugin.Sleezer.Core.Deezer;
using NzbDrone.Plugin.Sleezer.Core.Qobuz;
using NzbDrone.Plugin.Sleezer.HealthChecks;
using QobuzApiSharp.Models.User;
using Xunit;

namespace Sleezer.Tests;

public class StoreAccountCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 0, 0, 0, TimeSpan.Zero);

    // Shapes measured from Qobuz's user/login response; ids and dates are fake.
    private static Login QobuzLogin(string parameters, string subscription) => JsonConvert.DeserializeObject<Login>($$"""
        { "user": { "id": 1234567, "credential": { "label": "{{(parameters == "null" ? "" : "streaming-studio")}}", "parameters": {{parameters}} }, "subscription": {{subscription}} } }
        """)!;

    private const string StudioRights = """{ "lossy_streaming": true, "lossless_streaming": true, "hires_streaming": true }""";

    [Fact]
    public void Qobuz_studio_account_can_stream() =>
        Assert.Null(QobuzAccountCheck.StreamingProblem(QobuzLogin(StudioRights, """{ "offer": "studio", "end_date": "2026-10-27", "is_canceled": true }"""), Now));

    [Fact]
    public void Qobuz_lapsed_account_names_the_ended_subscription()
    {
        var problem = QobuzAccountCheck.StreamingProblem(QobuzLogin("null", """{ "offer": "studio", "end_date": "2026-09-22", "is_canceled": true }"""), Now);

        Assert.NotNull(problem);
        Assert.Contains("1234567", problem);
        Assert.Contains("studio subscription ended 2026-09-22", problem);
        Assert.Contains("30-second samples", problem);
    }

    [Fact]
    public void Qobuz_account_without_rights_or_a_past_subscription_is_flagged_without_an_end_date()
    {
        var problem = QobuzAccountCheck.StreamingProblem(QobuzLogin("""{ "lossy_streaming": false, "lossless_streaming": false }""", """{ "offer": "studio", "end_date": "2026-10-27" }"""), Now);

        Assert.NotNull(problem);
        Assert.DoesNotContain("ended", problem);
    }

    [Fact]
    public void Qobuz_lossy_only_rights_count_as_streaming() =>
        Assert.Null(QobuzAccountCheck.StreamingProblem(QobuzLogin("""{ "lossy_streaming": true, "lossless_streaming": false }""", "null"), Now));

    [Fact]
    public void Qobuz_not_signed_in_is_not_a_streaming_problem()
    {
        Assert.Null(QobuzAccountCheck.StreamingProblem(null, Now));
        Assert.Null(QobuzAccountCheck.StreamingProblem(new Login(), Now));
    }

    private static JObject DeezerUser(long userId, string options) => JObject.Parse($$"""
        { "USER": { "USER_ID": {{userId}}, "OPTIONS": {{options}} } }
        """);

    [Theory]
    [InlineData("web_lossless")]
    [InlineData("mobile_lossless")]
    [InlineData("web_hq")]
    [InlineData("mobile_hq")]
    public void Deezer_paid_plan_can_stream(string flag) =>
        Assert.Null(DeezerArlCheck.StreamingProblem(DeezerUser(7654321, $$"""{ "{{flag}}": true }""")));

    [Fact]
    public void Deezer_free_plan_is_flagged()
    {
        var problem = DeezerArlCheck.StreamingProblem(DeezerUser(7654321, """{ "web_hq": false, "web_lossless": false, "mobile_hq": false, "mobile_lossless": false }"""));

        Assert.NotNull(problem);
        Assert.Contains("7654321", problem);
    }

    [Fact]
    public void Deezer_rejected_arl_is_left_to_the_arl_check()
    {
        Assert.Null(DeezerArlCheck.StreamingProblem(DeezerUser(0, """{ "web_hq": false }""")));
        Assert.Null(DeezerArlCheck.StreamingProblem(null));
    }

    [Fact]
    public void Store_account_problems_become_one_error_with_a_wiki_link()
    {
        HealthCheck result = StoreAccountResult.From(typeof(StoreAccountCheckTests), ["Qobuz lapsed.", null, "Deezer free."]);

        Assert.Equal(HealthCheckResult.Error, result.Type);
        Assert.Equal("Qobuz lapsed. Deezer free.", result.Message);
        Assert.Equal("https://github.com/chodeus/sleezer#troubleshooting-%EF%B8%8F", result.WikiUrl?.FullUri);
    }

    [Fact]
    public void No_store_account_problem_is_ok() =>
        Assert.Equal(HealthCheckResult.Ok, StoreAccountResult.From(typeof(StoreAccountCheckTests), [null, null]).Type);
}
