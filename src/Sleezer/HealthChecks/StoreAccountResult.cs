using NzbDrone.Common.Http;
using NzbDrone.Core.HealthCheck;

namespace NzbDrone.Plugin.Sleezer.HealthChecks
{
    /// <summary>Builds the store account health result in a shape Lidarr can show.</summary>
    public static class StoreAccountResult
    {
        // GitHub's anchor keeps the heading emoji's U+FE0F.
        private static readonly HttpUri TroubleshootingUrl = new("https://github.com/chodeus/sleezer#troubleshooting-%EF%B8%8F");

        public static HealthCheck From(Type source, IEnumerable<string?> problems)
        {
            List<string> found = problems.OfType<string>().ToList();

            // Properties, not the result constructor: the plugins branch and develop disagree on its parameters.
            // WikiUrl is required: Lidarr's health API dereferences it without a null check.
            return found.Count == 0
                ? new HealthCheck(source)
                : new HealthCheck(source) { Type = HealthCheckResult.Error, Message = string.Join(" ", found), WikiUrl = TroubleshootingUrl };
        }
    }
}
