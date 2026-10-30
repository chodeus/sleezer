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

        /// <summary>The API key error for a request slskd refused; null for any other status.</summary>
        public static ValidationFailure? Refused(HttpStatusCode status, string? apiKey) =>
            status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? new ValidationFailure(nameof(SlskdSettings.ApiKey), string.IsNullOrWhiteSpace(apiKey)
                    ? "slskd's authentication is turned on, so it needs an API key."
                    : "slskd refused the API key.")
                : null;
    }
}
