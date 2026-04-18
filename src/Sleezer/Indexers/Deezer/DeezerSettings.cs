using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Indexers.Deezer
{
    public class DeezerIndexerSettingsValidator : AbstractValidator<DeezerIndexerSettings>
    {
        public DeezerIndexerSettingsValidator()
        {
            RuleFor(c => c.Arl)
                .NotEmpty().WithMessage("ARL token is required")
                .Length(100, 300).WithMessage("ARL token length looks invalid (expected ~192 characters)");
        }
    }

    public class DeezerIndexerSettings : IIndexerSettings
    {
        private static readonly DeezerIndexerSettingsValidator Validator = new DeezerIndexerSettingsValidator();

        private string _arl = "";

        [FieldDefinition(0, Label = "Arl", Type = FieldType.Textbox, HelpText = "The ARL cookie value from a premium/HI-FI Deezer account. Since March 2025 free accounts cannot download.")]
        public string Arl
        {
            get => _arl;
            set => _arl = value?.Trim() ?? "";
        }

        [FieldDefinition(1, Label = "Hide Albums With Missing Tracks", HelpText = "If an album has any unavailable tracks on Deezer, they will not be provided when searching.", Type = FieldType.Checkbox)]
        public bool HideAlbumsWithMissing { get; set; } = true;

        [FieldDefinition(2, Label = "Hide Clean Releases", HelpText = "Skip albums labelled as 'Clean' (explicit content censored). Non-clean releases are labelled [Explicit] in the title so you can filter with release profiles.", Type = FieldType.Checkbox)]
        public bool HideCleanReleases { get; set; } = true;

        [FieldDefinition(3, Label = "Allow MP3 320 Fallback for Missing FLAC Tracks", Type = FieldType.Checkbox, HelpText = "Offer FLAC releases even when some tracks lack a FLAC version on Deezer; missing tracks are downloaded as MP3 320 instead. Lidarr sees the mixed quality and may keep re-searching the MP3 tracks as upgrade candidates forever, since the FLAC simply doesn't exist on Deezer.", Advanced = true)]
        public bool AllowMp3FallbackForMissingFlac { get; set; } = false;

        [FieldDefinition(4, Type = FieldType.Number, Label = "Early Download Limit", Unit = "days", HelpText = "Time before release date Lidarr will download from this indexer, empty is no limit", Advanced = true)]
        public int? EarlyReleaseLimit { get; set; }

        // this is hardcoded so this doesn't need to exist except that it's required by the interface
        public string BaseUrl { get; set; } = "";

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
