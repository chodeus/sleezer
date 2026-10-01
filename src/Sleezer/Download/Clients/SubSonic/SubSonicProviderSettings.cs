using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using NzbDrone.Plugin.Sleezer.Indexers.SubSonic;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.SubSonic
{
    public class SubSonicProviderSettingsValidator : AbstractValidator<SubSonicProviderSettings>
    {
        public SubSonicProviderSettingsValidator()
        {
            // Validate DownloadPath
            RuleFor(x => x.DownloadPath)
                .IsValidPath()
                .WithMessage("Download path must be a valid directory.");

            RuleFor(x => x.IndexerId)
                .GreaterThanOrEqualTo(0);

            // Validate ConnectionRetries
            RuleFor(x => x.ConnectionRetries)
                .GreaterThanOrEqualTo(1)
                .LessThanOrEqualTo(10)
                .WithMessage("Connection retries must be between 1 and 10.");

            // Validate MaxParallelDownloads
            RuleFor(x => x.MaxParallelDownloads)
                .GreaterThanOrEqualTo(1)
                .LessThanOrEqualTo(5)
                .WithMessage("Max parallel downloads must be between 1 and 5.");

            // Validate MaxDownloadSpeed
            RuleFor(x => x.MaxDownloadSpeed)
                .GreaterThanOrEqualTo(0)
                .WithMessage("Max download speed must be greater than or equal to 0.")
                .LessThanOrEqualTo(100_000)
                .WithMessage("Max download speed must be less than or equal to 100 MB/s.");
        }
    }

    /// <summary>
    /// Configuration settings for the SubSonic download client
    /// </summary>
    public class SubSonicProviderSettings : IProviderConfig
    {
        private static readonly SubSonicProviderSettingsValidator Validator = new();

        [FieldDefinition(0, Label = "Download Path", Type = FieldType.Path, HelpText = "Directory where downloaded files will be saved")]
        public string DownloadPath { get; set; } = string.Empty;

        // Copied from the SubSonic indexer before each use (UseLogin); hidden, and the password is still masked by the API.
        [FieldDefinition(1, Label = "Server URL", Type = FieldType.Textbox, Hidden = HiddenType.Hidden)]
        public string ServerUrl { get; set; } = string.Empty;

        [FieldDefinition(2, Label = "Username", Type = FieldType.Textbox, Hidden = HiddenType.Hidden)]
        public string Username { get; set; } = string.Empty;

        [FieldDefinition(3, Label = "Password", Type = FieldType.Password, Privacy = PrivacyLevel.Password, Hidden = HiddenType.Hidden)]
        public string Password { get; set; } = string.Empty;

        [FieldDefinition(4, Label = "Use Token Authentication", Type = FieldType.Checkbox, Hidden = HiddenType.Hidden)]
        public bool UseTokenAuth { get; set; } = true;

        [FieldDefinition(5, Label = "Indexer", Type = FieldType.Select, SelectOptionsProviderAction = IndexerLogin.OptionsAction, HelpText = "The SubSonic indexer whose server and login this client uses. Automatic takes the only one.")]
        public int IndexerId { get; set; }

        [FieldDefinition(6, Type = FieldType.Number, Label = "Connection Retries", HelpText = "Number of times to retry failed connections", Advanced = true)]
        public int ConnectionRetries { get; set; } = 3;

        [FieldDefinition(7, Type = FieldType.Number, Label = "Max Parallel Downloads", HelpText = "Maximum number of downloads that can run simultaneously")]
        public int MaxParallelDownloads { get; set; } = 2;

        [FieldDefinition(8, Label = "Max Download Speed", Type = FieldType.Number, HelpText = "Set to 0 for unlimited speed. Limits download speed per file.", Unit = "KB/s", Advanced = true)]
        public int MaxDownloadSpeed { get; set; } = 0;

        [FieldDefinition(9, Label = "Preferred Audio Format", Type = FieldType.Select, SelectOptions = typeof(PreferredFormatEnum), HelpText = "Preferred audio format for transcoding (leave as 'Raw' for no transcoding)", Advanced = true)]
        public int PreferredFormat { get; set; } = (int)PreferredFormatEnum.Raw;

        [FieldDefinition(10, Label = "Max Bit Rate", Type = FieldType.Number, HelpText = "Maximum bit rate in kbps (0 for original quality)", Unit = "kbps", Advanced = true)]
        public int MaxBitRate { get; set; }

        [FieldDefinition(11, Type = FieldType.Number, Label = "Request Timeout", Hidden = HiddenType.Hidden)]
        public int RequestTimeout { get; set; } = 60;

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));

        /// <summary>Takes the server and login from the SubSonic indexer this client reuses.</summary>
        public void UseLogin(SubSonicIndexerSettings indexer)
        {
            ServerUrl = indexer.BaseUrl;
            Username = indexer.Username;
            Password = indexer.Password;
            UseTokenAuth = indexer.UseTokenAuth;
            RequestTimeout = indexer.RequestTimeout;
        }
    }

    /// <summary>
    /// Audio format options for SubSonic transcoding
    /// </summary>
    public enum PreferredFormatEnum
    {
        [FieldOption(Label = "No Transcoding", Hint = "Download audio in its original format.")]
        Raw = 0,

        [FieldOption(Label = "MP3", Hint = "Widely compatible lossy format.")]
        Mp3 = 1,

        [FieldOption(Label = "Opus", Hint = "Modern, efficient lossy codec.")]
        Opus = 2,

        [FieldOption(Label = "AAC", Hint = "High-quality lossy format with good compression, widely used.")]
        Aac = 3,

        [FieldOption(Label = "FLAC", Hint = "Lossless compression format that preserves original quality.")]
        Flac = 4
    }
}