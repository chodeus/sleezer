using NzbDrone.Core.ThingiProvider;

namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    /// <summary>Settings from a provider's saved definition, for an instance resolved without one.</summary>
    public static class StoredProviderSettings
    {
        // Keyed on the implementation name: the settings type alone cannot tell two providers apart.
        public static TSettings For<TSettings>(IEnumerable<ProviderDefinition> definitions, string implementation)
            where TSettings : IProviderConfig, new()
        {
            ProviderDefinition? definition = definitions.FirstOrDefault(d => d.Implementation == implementation);
            if (definition == null)
                return new();

            // Lidarr substitutes NullConfig when a saved row's ConfigContract no longer resolves;
            // defaulting there would run on settings we failed to load.
            return definition.Settings is TSettings settings
                ? settings
                : throw new InvalidOperationException(
                    $"{implementation} is saved with settings of type '{definition.ConfigContract}', not {typeof(TSettings).Name}.");
        }
    }
}
