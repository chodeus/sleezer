using System;
using System.IO;
using System.Net.Http;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using QobuzApiSharp.Exceptions;
using QobuzApiSharp.Models.Content;

namespace NzbDrone.Plugin.Sleezer.Core.Qobuz
{
    /// <summary>Decides what one failed Qobuz track attempt means for the rest of that track.</summary>
    public static class QobuzTrackAttempt
    {
        public static void EnsureFullTrack(FileUrl urls, string trackId)
        {
            if (urls.Sample ?? false)
                throw new QobuzSampleException($"Qobuz served only a 30-second sample of track {trackId}.");
        }

        public static QobuzAttemptOutcome Classify(Exception ex, int attempt, int maxAttempts) => ex switch
        {
            // A rights answer, not a transient one: every retry gets the same sample.
            QobuzSampleException => QobuzAttemptOutcome.Skip,
            ApiErrorResponseException { ResponseStatusCode: "404" } => QobuzAttemptOutcome.TryNextQuality,
            _ when attempt < maxAttempts && IsTransient(ex) => QobuzAttemptOutcome.Retry,
            _ => QobuzAttemptOutcome.Fail,
        };

        // Only a dropped connection, a timeout, a 429 or 5xx (an unreadable error page included) or a short file can
        // change on another try. The caller rethrows the user's own cancellation first, so a cancellation here is a timeout.
        private static bool IsTransient(Exception ex) => ex switch
        {
            HttpRequestException { StatusCode: null } => true,
            HttpRequestException { StatusCode: { } status } => TransientStatus.IsTransient(status),
            ApiErrorResponseException { ResponseStatusCode: var code } => int.TryParse(code, out int status) && TransientStatus.IsTransient(status),
            ApiResponseParseErrorException { StatusCode: { } status } => TransientStatus.IsTransient(status),
            IOException or TimeoutException or OperationCanceledException => true,
            _ => false,
        };
    }

    public enum QobuzAttemptOutcome
    {
        Retry,
        TryNextQuality,
        Skip,
        Fail,
    }

    public class QobuzSampleException : Exception
    {
        public QobuzSampleException(string message, Exception? inner = null) : base(message, inner) { }
    }
}
