using System;
using NzbDrone.Plugin.Sleezer.Core.Qobuz;
using QobuzApiSharp.Exceptions;
using QobuzApiSharp.Models;
using QobuzApiSharp.Models.Content;
using Xunit;

namespace Sleezer.Tests;

public class QobuzTrackAttemptTests
{
    private const int MaxAttempts = 3;

    [Fact]
    public void A_sample_is_skipped_on_the_first_attempt_without_retrying()
    {
        var ex = Record.Exception(() => QobuzTrackAttempt.EnsureFullTrack(new FileUrl { Sample = true }, "123"));

        Assert.NotNull(ex);
        Assert.Equal(QobuzAttemptOutcome.Skip, QobuzTrackAttempt.Classify(ex!, attempt: 1, MaxAttempts));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void EnsureFullTrack_accepts_a_full_track(bool? sample)
    {
        Assert.Null(Record.Exception(() => QobuzTrackAttempt.EnsureFullTrack(new FileUrl { Sample = sample }, "123")));
    }

    [Fact]
    public void Classify_moves_to_the_next_quality_on_a_404()
    {
        var notFound = new ApiErrorResponseException("not found", string.Empty, new QobuzApiStatusResponse { Code = "404" });

        Assert.Equal(QobuzAttemptOutcome.TryNextQuality, QobuzTrackAttempt.Classify(notFound, attempt: 1, MaxAttempts));
    }

    [Theory]
    [InlineData(1, QobuzAttemptOutcome.Retry)]
    [InlineData(2, QobuzAttemptOutcome.Retry)]
    [InlineData(3, QobuzAttemptOutcome.Fail)]
    public void Classify_retries_any_other_error_until_the_last_attempt(int attempt, QobuzAttemptOutcome expected)
    {
        Assert.Equal(expected, QobuzTrackAttempt.Classify(new InvalidOperationException("stalled"), attempt, MaxAttempts));
    }
}
