using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using Xunit;

namespace Sleezer.Tests;

// Covers the resolver behind every provider's ActiveSettings.
public class StoredProviderSettingsTests
{
    private sealed class Config : IProviderConfig
    {
        public int Interval { get; init; } = 60;

        public NzbDroneValidationResult Validate() => new();
    }

    private static MetadataDefinition D(string implementation, IProviderConfig settings) => new()
    {
        Implementation = implementation,
        Settings = settings
    };

    // Keyed on the implementation name, not on the settings type.
    [Fact]
    public void Returns_the_saved_settings_of_the_named_implementation()
    {
        Config saved = new() { Interval = 5 };
        Config decoy = new() { Interval = 99 };

        Assert.Same(saved, StoredProviderSettings.For<Config>([D("Other", decoy), D("Wanted", saved)], "Wanted"));
    }

    [Fact]
    public void Defaults_when_no_definition_exists_yet()
    {
        Assert.Equal(60, StoredProviderSettings.For<Config>([], "Wanted").Interval);
    }

    // Defaults here would run on settings that failed to load, not on an absent definition.
    [Fact]
    public void Fails_closed_when_the_stored_settings_are_another_type()
    {
        Assert.Throws<InvalidOperationException>(
            () => StoredProviderSettings.For<Config>([D("Wanted", NullConfig.Instance)], "Wanted"));
    }
}
