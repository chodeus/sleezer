using System.Net;
using NLog;
using NzbDrone.Core.Download.Clients.Deezer.Queue;
using NzbDrone.Plugin.Sleezer.Core.Deezer;
using Xunit;

namespace Sleezer.Tests;

public class DeezerTrackAttemptTests
{
    private static readonly Logger Log = LogManager.CreateNullLogger();

    public static TheoryData<Exception> Transient => new()
    {
        new IOException("connection reset by peer"),
        new HttpRequestException("Connection refused (cdn.invalid:443)"),
        new HttpRequestException("Response status code does not indicate success: 503", null, HttpStatusCode.ServiceUnavailable),
        new HttpRequestException("Response status code does not indicate success: 429", null, HttpStatusCode.TooManyRequests),
        new TimeoutException("Deezer track 1 stalled for over 10 minutes; aborting."),
        new TrackIncompleteException("Deezer track 1 at FLAC truncated: got 10 of expected 100 bytes (10%)."),
    };

    public static TheoryData<Exception> Lasting => new()
    {
        new InsufficientLicenseRightsException("License check failed"),
        new GeoRestrictionException("Track 1 is not available in your country (code 2002)."),
        new TrackUnavailableException("Deezer reports no media sources for track 1."),
        new InvalidOperationException("wrapped", new Exception("License token has no sufficient rights")),
        new AggregateException(new Exception("wrong geolocation")),
        new Exception("wrapped", new TrackUnavailableException("Deezer reports no media sources for track 1.")),
        new InvalidOperationException("No Deezer license token available — the ARL session is not initialized or was rejected."),
        new HttpRequestException("Response status code does not indicate success: 401", null, HttpStatusCode.Unauthorized),
        new UnauthorizedAccessException("Access to the path '/downloads/deezer/1.flac' is denied."),
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
    [MemberData(nameof(Lasting))]
    public void A_failure_another_try_cannot_change_is_not_retried(Exception ex)
    {
        Assert.False(DeezerTrackAttempt.ShouldRetry(ex, attempt: 1, maxAttempts: 3));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public void An_empty_or_short_file_is_deleted_and_worth_another_try(int size)
    {
        string path = TempFile(size);
        try
        {
            var ex = Assert.Throws<TrackIncompleteException>(() => DeezerTrackAttempt.EnsureCompleteFile(path, "track 1 at FLAC", 100, Log));

            Assert.False(File.Exists(path));
            Assert.True(DeezerTrackAttempt.ShouldRetry(ex, attempt: 1, maxAttempts: 3));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(95, 100)]
    [InlineData(10, 0)]
    public void A_file_within_ten_percent_or_without_a_reported_size_is_kept(int size, long expectedSize)
    {
        string path = TempFile(size);
        try
        {
            DeezerTrackAttempt.EnsureCompleteFile(path, "track 1 at FLAC", expectedSize, Log);

            Assert.True(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string TempFile(int size)
    {
        string path = Path.Combine(Path.GetTempPath(), $"sleezer-{Guid.NewGuid():N}.flac");
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }
}
