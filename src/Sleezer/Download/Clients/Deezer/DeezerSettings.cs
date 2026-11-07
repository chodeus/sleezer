using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;

namespace NzbDrone.Core.Download.Clients.Deezer
{
    public class DeezerSettingsValidator : AbstractValidator<DeezerSettings>
    {
        public DeezerSettingsValidator()
        {
            RuleFor(x => x.DownloadPath).IsValidPath();
        }
    }

    public class DeezerSettings : IProviderConfig
    {
        private static readonly DeezerSettingsValidator Validator = new DeezerSettingsValidator();

        [FieldDefinition(0, Label = "Download Path", Type = FieldType.Textbox)]
        public string DownloadPath { get; set; } = "";

        // Moved to the Lyrics metadata entry; hidden so the one-time copy can read them.
        [FieldDefinition(1, Label = "Save Synced Lyrics", Type = FieldType.Checkbox, Hidden = HiddenType.Hidden)]
        public bool SaveSyncedLyrics { get; set; } = false;

        [FieldDefinition(2, Label = "Use LRCLIB as Backup Lyric Provider", Type = FieldType.Checkbox, Hidden = HiddenType.Hidden)]
        public bool UseLRCLIB { get; set; } = false;

        [FieldDefinition(3, Label = "Allow Track Substitution", HelpText = "Download Deezer's alternative for an unavailable track when its ISRC matches, or its title, version, duration and explicit flag do.", Type = FieldType.Checkbox)]
        public bool AllowTrackSubstitution { get; set; } = true;

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
