using NzbDrone.Core.ThingiProvider;

namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    /// <summary>Settings from a provider's saved definition, for an instance resolved without one.</summary>
    public static class StoredProviderSettings
    {
        // Keyed on the implementation name: the settings type alone cannot tell two providers apart.
        public static TSettings For<TSettings>(IEnumerable<ProviderDefinition> definitions, string implementation)
            where TSettings : IProviderConfig, new() =>
            definitions.FirstOrDefault(d => d.Implementation == implementation)?.Settings is TSettings settings ? settings : new();
    }
}
