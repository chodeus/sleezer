using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Deezer;
using NzbDrone.Core.Indexers.Qobuz;
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
    [CheckOn(typeof(ProviderDeletedEvent<IIndexer>))]
    public class StoreAccountHealthCheck : HealthCheckBase
    {
        private static readonly TimeSpan RightsMaxAge = TimeSpan.FromHours(6);

        private readonly IIndexerFactory _indexerFactory;

        public StoreAccountHealthCheck(IIndexerFactory indexerFactory, ILocalizationService localizationService)
            : base(localizationService)
        {
            _indexerFactory = indexerFactory;
        }

        public override HealthCheck Check()
        {
            // Each store keeps one shared session, which may belong to another account or a deleted
            // indexer: it is judged only when an enabled indexer configures that same account.
            List<IndexerDefinition> enabled = _indexerFactory.All().Where(d => d.Enable).ToList();
            var problems = new List<string?>();

            QobuzAPI? qobuzSession = QobuzAPI.Instance;
            if (qobuzSession != null &&
                enabled.Select(d => d.Settings).OfType<QobuzIndexerSettings>().FirstOrDefault(qobuzSession.IsFor) is { } qobuz)
            {
                QobuzAPI.RefreshRightsIfStale(qobuz, RightsMaxAge);
                problems.Add(QobuzAccountCheck.StreamingProblem(qobuzSession.Login, DateTimeOffset.UtcNow));
            }

            DeezerAPI? deezerSession = DeezerAPI.Instance;
            if (deezerSession != null &&
                enabled.Select(d => d.Settings).OfType<DeezerIndexerSettings>().Any(d => deezerSession.IsFor(d.Arl)))
            {
                problems.Add(DeezerArlCheck.StreamingProblem(deezerSession.Client.GWApi.ActiveUserData));
            }

            List<string> found = problems.Where(p => p != null).Select(p => p!).ToList();

            return found.Count == 0
                ? new HealthCheck(GetType())
                : new HealthCheck(GetType(), HealthCheckResult.Error, string.Join(" ", found));
        }
    }
}
