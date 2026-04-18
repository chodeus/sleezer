using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;
using NzbDrone.Plugin.Sleezer.Indexers.TripleTriple;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.TripleTriple
{
    public class TripleTripleProviderSettingsValidator : AbstractValidator<TripleTripleProviderSettings>
    {
        public TripleTripleProviderSettingsValidator()
        {
            RuleFor(x => x.DownloadPath)
                .IsValidPath()
                .WithMessage("Download path must be a valid directory.");

            RuleFor(x => x.BaseUrl)
                .NotEmpty().WithMessage("Base URL is required.")
                .Must(url => Uri.IsWellFormedUriString(url, UriKind.Absolute))
                .WithMessage("Base URL must be a valid URL.");

            RuleFor(x => x.ConnectionRetries)
                .GreaterThanOrEqualTo(1)
                .LessThanOrEqualTo(10)
                .WithMessage("Connection retries must be between 1 and 10.");

            RuleFor(x => x.MaxParallelDownloads)
                .GreaterThanOrEqualTo(1)
                .LessThanOrEqualTo(5)
                .WithMessage("Max parallel downloads must be between 1 and 5.");

            RuleFor(x => x.MaxDownloadSpeed)
                .GreaterThanOrEqualTo(0)
                .WithMessage("Max download speed must be greater than or equal to 0.")
                .LessThanOrEqualTo(100_000)
                .WithMessage("Max download speed must be less than or equal to 100 MB/s.");

            RuleFor(x => x.CoverSize)
                .InclusiveBetween(100, 2000)
                .WithMessage("Cover size must be between 100 and 2000 pixels.");
        }
    }

    public class TripleTripleProviderSettings : IProviderConfig
    {
        private static readonly TripleTripleProviderSettingsValidator Validator = new();

        public TripleTripleProviderSettings()
        {
            ConnectionRetries = 3;
            MaxParallelDownloads = 2;
            MaxDownloadSpeed = 0;
            CoverSize = 1200;
            DownloadLyrics = true;
            CreateLrcFile = true;
            EmbedLyrics = false;
        }

        [FieldDefinition(0, Label = "Download Path", Type = FieldType.Path, HelpText = "Directory where downloaded files will be saved")]
        public string DownloadPath { get; set; } = string.Empty;

        [FieldDefinition(1, Label = "Base URL", Type = FieldType.Textbox, HelpText = "URL of the T2Tunes API instance", Placeholder = "https://T2Tunes.site")]
        public string BaseUrl { get; set; } = string.Empty;

        [FieldDefinition(2, Label = "Country", Type = FieldType.Select, SelectOptions = typeof(TripleTripleCountry), HelpText = "Country code for Amazon Music region")]
        public int CountryCode { get; set; } = (int)TripleTripleCountry.US;

        [FieldDefinition(3, Label = "Preferred Codec", Type = FieldType.Select, SelectOptions = typeof(TripleTripleCodec), HelpText = "Audio codec preference for downloads")]
        public int Codec { get; set; } = (int)TripleTripleCodec.FLAC;

        [FieldDefinition(4, Label = "Download Lyrics", Type = FieldType.Checkbox, HelpText = "Download lyrics when available")]
        public bool DownloadLyrics { get; set; }

        [FieldDefinition(5, Label = "Create LRC File", Type = FieldType.Checkbox, HelpText = "Create .lrc file with synced lyrics alongside audio file")]
        public bool CreateLrcFile { get; set; }

        [FieldDefinition(6, Label = "Embed Lyrics in Audio", Type = FieldType.Checkbox, HelpText = "Embed lyrics directly in audio file metadata")]
        public bool EmbedLyrics { get; set; }

        [FieldDefinition(7, Label = "Cover Size", Type = FieldType.Number, HelpText = "Album cover image size in pixels", Unit = "px", Advanced = true)]
        public int CoverSize { get; set; }

        [FieldDefinition(8, Type = FieldType.Number, Label = "Connection Retries", HelpText = "Number of times to retry failed connections", Advanced = true)]
        public int ConnectionRetries { get; set; }

        [FieldDefinition(9, Type = FieldType.Number, Label = "Max Parallel Downloads", HelpText = "Maximum number of downloads that can run simultaneously")]
        public int MaxParallelDownloads { get; set; }

        [FieldDefinition(10, Label = "Max Download Speed", Type = FieldType.Number, HelpText = "Set to 0 for unlimited speed. Limits download speed per file.", Unit = "KB/s", Advanced = true)]
        public int MaxDownloadSpeed { get; set; }

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}
