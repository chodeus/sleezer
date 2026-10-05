using System.Net;
using NzbDrone.Core.Download.Clients.Deezer.Queue;
using NzbDrone.Plugin.Sleezer.Core.Deezer;
using Xunit;

namespace Sleezer.Tests;

public class DeezerTrackAttemptTests
{
    public static TheoryData<Exception> Transient => new()
    {
        new IOException("connection reset by peer"),
        new HttpRequestException("Response status code does not indicate success: 503", null, HttpStatusCode.ServiceUnavailable),
        new TimeoutException("Deezer track 1 stalled for over 10 minutes; aborting."),
        new InvalidOperationException("Deezer track 1 at FLAC truncated: got 10 of expected 100 bytes (10%)."),
    };

    public static TheoryData<Exception> Final => new()
    {
        new InsufficientLicenseRightsException("License check failed"),
        new GeoRestrictionException("Track 1 is not available in your country (code 2002)."),
        new TrackUnavailableException("Deezer reports no media sources for track 1."),
        new InvalidOperationException("wrapped", new Exception("License token has no sufficient rights")),
        new AggregateException(new Exception("wrong geolocation")),
    };

    [Theory]
    [MemberData(nameof(Transient))]
    public void A_transient_failure_is_retried_until_the_last_attempt(Exception ex)
    {
        Assert.True(DeezerTrackAttempt.ShouldRetry(ex, attempt: 1, maxAttempts: 3));
        Assert.True(DeezerTrackAttempt.ShouldRetry(ex, attempt: 2, maxAttempts: 3));
        Assert.False(DeezerTrackAttempt.ShouldRetry(ex, attempt: 3, maxAttempts: 3));
    }

    [Theory]
    [MemberData(nameof(Final))]
    public void A_rights_or_region_answer_is_never_retried(Exception ex)
    {
        Assert.False(DeezerTrackAttempt.ShouldRetry(ex, attempt: 1, maxAttempts: 3));
    }
}
