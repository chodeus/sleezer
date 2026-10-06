using System;
using System.Net;
using NzbDrone.Plugin.Sleezer.Core.Qobuz;
using QobuzApiSharp.Exceptions;
using QobuzApiSharp.Models;
using QobuzApiSharp.Models.Content;
using QobuzApiSharp.Service;
using Xunit;

namespace Sleezer.Tests;

public class QobuzTrackAttemptTests
{
    private const int MaxAttempts = 3;

    public static TheoryData<Exception> Transient => new()
    {
        new HttpRequestException("Connection refused (streaming.invalid:443)"),
        new HttpRequestException("Response status code does not indicate success: 503", null, HttpStatusCode.ServiceUnavailable),
        new HttpRequestException("Response status code does not indicate success: 429", null, HttpStatusCode.TooManyRequests),
        ApiError("500"),
        UnreadablePage(HttpStatusCode.BadGateway),
        new IOException("Incomplete download for Qobuz track 1: server reported 100 bytes but 10 were written."),
        new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing."),
    };

    public static TheoryData<Exception> Lasting => new()
    {
        ApiError("401"),
        ApiError("400"),
        UnreadablePage(HttpStatusCode.Unauthorized),
        UnreadablePage(HttpStatusCode.OK),
        new HttpRequestException("Response status code does not indicate success: 403", null, HttpStatusCode.Forbidden),
        new UnauthorizedAccessException("Access to the path '/downloads/qobuz/1.flac.part' is denied."),
        new InvalidOperationException("Qobuz track 1 has no media source at FLAC_LOSSLESS."),
    };

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
        Assert.Equal(QobuzAttemptOutcome.TryNextQuality, QobuzTrackAttempt.Classify(ApiError("404"), attempt: 1, MaxAttempts));
    }

    [Theory]
    [MemberData(nameof(Transient))]
    public void Classify_retries_a_transient_failure_until_the_last_attempt(Exception ex)
    {
        Assert.Equal(QobuzAttemptOutcome.Retry, QobuzTrackAttempt.Classify(ex, attempt: 1, MaxAttempts));
        Assert.Equal(QobuzAttemptOutcome.Retry, QobuzTrackAttempt.Classify(ex, attempt: 2, MaxAttempts));
        Assert.Equal(QobuzAttemptOutcome.Fail, QobuzTrackAttempt.Classify(ex, attempt: 3, MaxAttempts));
    }

    [Theory]
    [MemberData(nameof(Lasting))]
    public void Classify_fails_at_once_when_another_try_cannot_change_the_answer(Exception ex)
    {
        Assert.Equal(QobuzAttemptOutcome.Fail, QobuzTrackAttempt.Classify(ex, attempt: 1, MaxAttempts));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadGateway, HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.OK, null)]
    public void An_unreadable_body_keeps_the_status_only_of_a_failed_response(HttpStatusCode sent, HttpStatusCode? kept)
    {
        Assert.Equal(kept, UnreadablePage(sent).StatusCode);
    }

    // Through the real parser, so the status is the one DeserializeResponse attaches.
    private static ApiResponseParseErrorException UnreadablePage(HttpStatusCode status)
    {
        using var response = new HttpResponseMessage(status) { Content = new StringContent("<html><body>Bad Gateway</body></html>") };

        return Assert.IsType<ApiResponseParseErrorException>(
            Record.Exception(() => QobuzApiHelper.DeserializeResponse<QobuzApiStatusResponse>(response)));
    }

    private static ApiErrorResponseException ApiError(string code) =>
        new("API request failed for endpoint track/getFileUrl.", string.Empty, new QobuzApiStatusResponse { Code = code });
}
