using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.ThingiProvider;

namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    /// <summary>Reads the Metadata entries that hold settings shared by every Sleezer indexer and client.</summary>
    public static class SharedSettings
    {
        /// <summary>The entry holding <typeparamref name="T"/>, or null when Lidarr has none.</summary>
        public static MetadataDefinition? Definition<T>(IMetadataFactory factory)
            where T : class, IProviderConfig
        {
            try
            {
                return factory.All().FirstOrDefault(d => d.Settings is T);
            }
            catch (Exception ex)
            {
                // Not swallowed: callers that fail closed must not mistake an unreadable entry for an empty one.
                throw new InvalidOperationException($"Could not read the shared {typeof(T).Name}.", ex);
            }
        }

        public static T? Read<T>(IMetadataFactory factory)
            where T : class, IProviderConfig =>
            Definition<T>(factory)?.Settings as T;
    }
}
