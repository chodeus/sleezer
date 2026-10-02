using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Plugin.Sleezer.Metadata.Lyrics
{
    public class LyricsSettings : IProviderConfig
    {
        private static readonly InlineValidator<LyricsSettings> Validator = new();

        [FieldDefinition(0, Label = "Save Synced Lyrics", Type = FieldType.Checkbox,
            HelpText = "Save time-synced lyrics as an .lrc file beside each track downloaded from Deezer, Qobuz or Tidal.",
            HelpTextWarning = "Enable this entry to use the options here. Deezer and Tidal embed their own plain lyrics either way.")]
        public bool SaveSyncedLyrics { get; set; }

        [FieldDefinition(1, Label = "Use LRCLIB", Type = FieldType.Checkbox,
            HelpText = "Fetch lyrics from LRCLIB when the store has none. Qobuz supplies no lyrics of its own, so this is its only source.")]
        public bool UseLRCLIB { get; set; }

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));

        /// <summary>The options as they apply: all off while the entry is disabled.</summary>
        public static LyricsSettings Effective(MetadataDefinition? definition) =>
            definition is { Enable: true, Settings: LyricsSettings settings } ? settings : new LyricsSettings();
    }
}
