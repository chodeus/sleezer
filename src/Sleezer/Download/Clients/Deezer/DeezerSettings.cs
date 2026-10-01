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

        [FieldDefinition(3, Label = "Allow Track Substitution", HelpText = "When a track is unavailable, download Deezer's own alternative for the same recording (matched by ISRC, or an exact title/version/duration match). Never a search — a different recording is always refused.", Type = FieldType.Checkbox)]
        public bool AllowTrackSubstitution { get; set; } = true;

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
