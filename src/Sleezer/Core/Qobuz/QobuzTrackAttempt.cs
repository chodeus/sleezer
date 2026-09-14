using System;
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
            _ when attempt < maxAttempts => QobuzAttemptOutcome.Retry,
            _ => QobuzAttemptOutcome.Fail,
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
