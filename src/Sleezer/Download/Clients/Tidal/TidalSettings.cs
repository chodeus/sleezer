using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;

namespace NzbDrone.Core.Download.Clients.Tidal
{
    public class TidalSettingsValidator : AbstractValidator<TidalSettings>
    {
        public TidalSettingsValidator()
        {
            RuleFor(x => x.DownloadPath).IsValidPath();
        }
    }

    public class TidalSettings : IProviderConfig
    {
        private static readonly TidalSettingsValidator Validator = new();

        [FieldDefinition(0, Label = "Download Path", Type = FieldType.Textbox)]
        public string DownloadPath { get; set; } = "";

        // Moved to the FFmpeg metadata entry; hidden so the one-time copy can read them.
        [FieldDefinition(1, Label = "Extract FLAC From M4A", Type = FieldType.Checkbox, Hidden = HiddenType.Hidden)]
        public bool ExtractFlac { get; set; } = false;

        [FieldDefinition(2, Label = "Re-encode AAC into MP3", Type = FieldType.Checkbox, Hidden = HiddenType.Hidden)]
        public bool ReEncodeAAC { get; set; } = false;

        // Moved to the Lyrics metadata entry; hidden so the one-time copy can read them.
        [FieldDefinition(3, Label = "Save Synced Lyrics", Type = FieldType.Checkbox, Hidden = HiddenType.Hidden)]
        public bool SaveSyncedLyrics { get; set; } = false;

        [FieldDefinition(4, Label = "Use LRCLIB as Backup Lyric Provider", Type = FieldType.Checkbox, Hidden = HiddenType.Hidden)]
        public bool UseLRCLIB { get; set; } = false;

        [FieldDefinition(5, Label = "Download Delay", HelpText = "When downloading many tracks, Tidal may rate-limit you. This will add a delay between track downloads to help prevent this.", Type = FieldType.Checkbox)]
        public bool DownloadDelay { get; set; } = false;

        [FieldDefinition(6, Label = "Download Delay Minimum", HelpText = "Minimum download delay, in seconds.", Type = FieldType.Number, Advanced = true)]
        public float DownloadDelayMin { get; set; } = 3.0f;

        [FieldDefinition(7, Label = "Download Delay Maximum", HelpText = "Maximum download delay, in seconds.", Type = FieldType.Number, Advanced = true)]
        public float DownloadDelayMax { get; set; } = 5.0f;

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
