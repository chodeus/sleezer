using FluentValidation;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;

namespace NzbDrone.Core.Download.Clients.Bandcamp
{
    public class BandcampDownloadSettingsValidator : AbstractValidator<BandcampDownloadSettings>
    {
        public BandcampDownloadSettingsValidator()
        {
            RuleFor(c => c.Cookies).NotEmpty()
                .WithMessage("Session cookies are required to download purchased albums from Bandcamp");

            RuleFor(c => c.DownloadPath).NotEmpty()
                .WithMessage("Download path is required");

            RuleFor(c => c.DownloadPath).IsValidPath()
                .When(c => !c.DownloadPath.IsNullOrWhiteSpace());
        }
    }

    public class BandcampDownloadSettings : IProviderConfig
    {
        private static readonly BandcampDownloadSettingsValidator Validator = new BandcampDownloadSettingsValidator();

        public BandcampDownloadSettings()
        {
            DownloadPath = "";
            Cookies = "";
        }

        [FieldDefinition(0, Label = "Session Cookies", Type = FieldType.Textbox, HelpText = "The 'identity' cookie from bandcamp.com (DevTools → Application → Cookies).", Privacy = PrivacyLevel.Password)]
        public string Cookies { get; set; }

        [FieldDefinition(1, Label = "Download Path", Type = FieldType.Path, HelpText = "Directory where Bandcamp downloads will be saved before import")]
        public string DownloadPath { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
