using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Localization;
using NzbDrone.Core.ThingiProvider.Events;
using NzbDrone.Plugin.Sleezer.Core.Deezer;
using NzbDrone.Plugin.Sleezer.Core.Qobuz;
using NzbDrone.Plugin.Sleezer.Deezer;
using NzbDrone.Plugin.Sleezer.Qobuz;

namespace NzbDrone.Plugin.Sleezer.HealthChecks
{
    /// <summary>Flags a signed-in store account that can no longer stream what Sleezer downloads.</summary>
    [CheckOn(typeof(ProviderAddedEvent<IIndexer>))]
    [CheckOn(typeof(ProviderUpdatedEvent<IIndexer>))]
    public class StoreAccountHealthCheck : HealthCheckBase
    {
        public StoreAccountHealthCheck(ILocalizationService localizationService)
            : base(localizationService)
        {
        }

        public override HealthCheck Check()
        {
            var problems = new List<string?>
            {
                QobuzAccountCheck.StreamingProblem(QobuzAPI.Instance?.Login, DateTimeOffset.UtcNow),
                DeezerArlCheck.StreamingProblem(DeezerAPI.Instance?.Client.GWApi.ActiveUserData)
            }.Where(p => p != null).ToList();

            return problems.Count == 0
                ? new HealthCheck(GetType())
                : new HealthCheck(GetType(), HealthCheckResult.Error, string.Join(" ", problems));
        }
    }
}
