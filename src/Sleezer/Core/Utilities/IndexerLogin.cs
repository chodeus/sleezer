using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.ThingiProvider;

namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    /// <summary>Finds the indexer whose login a Sleezer download client reuses.</summary>
    public static class IndexerLogin
    {
        public const string OptionsAction = "getLoginIndexers";

        /// <summary>The chosen indexer, or the only one of its kind when none is chosen (id 0); disabled indexers count.</summary>
        public static TSettings Find<TSettings>(IEnumerable<ProviderDefinition> indexers, int indexerId, string kind)
            where TSettings : class, IProviderConfig
        {
            List<ProviderDefinition> matching = indexers.Where(d => d.Settings is TSettings).ToList();

            if (indexerId > 0)
            {
                return matching.FirstOrDefault(d => d.Id == indexerId)?.Settings as TSettings
                    ?? throw new DownloadClientException($"The {kind} indexer this client was set to use no longer exists. Pick one under Indexer.");
            }

            return matching.Count switch
            {
                1 => (TSettings)matching[0].Settings,
                0 => throw new DownloadClientException($"Add a {kind} indexer first: this client uses its URL and login."),
                _ => throw new DownloadClientException($"There are {matching.Count} {kind} indexers. Pick the one this client uses under Indexer.")
            };
        }

        /// <summary>The Indexer dropdown: Automatic, then every indexer of the kind by name.</summary>
        public static object Options<TSettings>(IEnumerable<ProviderDefinition> indexers)
            where TSettings : class, IProviderConfig => new
            {
                options = indexers
                    .Where(d => d.Settings is TSettings)
                    .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(d => new { Value = d.Id, Name = d.Name })
                    .Prepend(new { Value = 0, Name = "Automatic (the only one)" })
                    .ToList()
            };
    }
}
