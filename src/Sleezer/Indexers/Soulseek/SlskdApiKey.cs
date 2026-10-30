using System.Net;
using FluentValidation.Results;
using NzbDrone.Common.Http;

namespace NzbDrone.Plugin.Sleezer.Indexers.Soulseek
{
    public static class SlskdApiKey
    {
        /// <summary>Sends the API key when one is set; slskd with authentication turned off needs none.</summary>
        public static HttpRequestBuilder WithApiKey(this HttpRequestBuilder builder, string? apiKey) =>
            string.IsNullOrWhiteSpace(apiKey) ? builder : builder.SetHeader("X-API-KEY", apiKey);

        /// <summary>The API key error for a refused request (401 or 403), which a proxy in front of slskd can also send; null for any other status.</summary>
        public static ValidationFailure? Refused(HttpStatusCode status, string? apiKey) =>
            status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? new ValidationFailure(nameof(SlskdSettings.ApiKey), string.IsNullOrWhiteSpace(apiKey)
                    ? $"The request was refused ({(int)status} {status}). If slskd's authentication is on, set its API key; otherwise check any proxy in front of slskd."
                    : $"The request was refused ({(int)status} {status}). Check the API key, and any proxy in front of slskd.")
                : null;
    }
}
