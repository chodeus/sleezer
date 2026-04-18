using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;
using System.Text.RegularExpressions;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek
{
    internal class SlskdProviderSettingsValidator : AbstractValidator<SlskdProviderSettings>
    {
        public SlskdProviderSettingsValidator()
        {
            // Base URL validation
            RuleFor(c => c.BaseUrl)
                .ValidRootUrl()
                .Must(url => !url.EndsWith('/'))
                .WithMessage("Base URL must not end with a slash ('/').");

            // API Key validation
            RuleFor(c => c.ApiKey)
                .NotEmpty()
                .WithMessage("API Key is required.");

            // Timeout validation (only if it has a value)
            RuleFor(c => c.Timeout)
                .GreaterThanOrEqualTo(0.1)
                .WithMessage("Timeout must be at least 0.1 hours.")
                .When(c => c.Timeout.HasValue);

            // RetryAttempts validation
            RuleFor(c => c.RetryAttempts)
                .InclusiveBetween(0, 10)
                .WithMessage("Retry attempts must be between 0 and 10.");

            RuleFor(c => c.BanUserAfterCorruptCount)
                .InclusiveBetween(0, 50)
                .WithMessage("Ban user after corrupt count must be between 0 (disabled) and 50.");

            RuleFor(c => c.MaxQueuePositionBeforeCancel)
                .InclusiveBetween(0, 10000)
                .WithMessage("Max queue position must be between 0 (disabled) and 10000.");

            RuleFor(c => c.MaxQueueWaitMinutes)
                .InclusiveBetween(0, 1440)
                .WithMessage("Max queue wait must be between 0 (disabled) and 1440 minutes (24h).");

            RuleFor(c => c.StallTimeoutMinutes)
                .InclusiveBetween(0, 240)
                .WithMessage("Stall timeout must be between 0 (disabled) and 240 minutes (4h).");
        }
    }

    public partial class SlskdProviderSettings : IProviderConfig
    {
        private static readonly SlskdProviderSettingsValidator Validator = new();
        private string? _host;

        [FieldDefinition(0, Label = "URL", Type = FieldType.Url, Placeholder = "http://localhost:5030", HelpText = "The URL of your Slskd instance.")]
        public string BaseUrl { get; set; } = "http://localhost:5030";

        [FieldDefinition(1, Label = "API Key", Type = FieldType.Textbox, Privacy = PrivacyLevel.ApiKey, HelpText = "The API key for your Slskd instance. You can find or set this in the Slskd's settings under 'Options'.", Placeholder = "Enter your API key")]
        public string ApiKey { get; set; } = string.Empty;

        [FieldDefinition(3, Label = "Timeout", Type = FieldType.Textbox, HelpText = "Specify the maximum time to wait for a response from the Slskd instance before timing out. Fractional values are allowed (e.g., 1.5 for 1 hour and 30 minutes). Set leave blank for no timeout.", Unit = "hours", Advanced = true, Placeholder = "Enter timeout in hours")]
        public double? Timeout { get; set; }

        [FieldDefinition(4, Label = "Retry Attempts", Type = FieldType.Number, HelpText = "The number of times to retry downloading a file if it fails.", Advanced = true, Placeholder = "Enter retry attempts")]
        public int RetryAttempts { get; set; } = 1;

        [FieldDefinition(5, Label = "Inclusive", Type = FieldType.Checkbox, HelpText = "Include all downloads made in Slskd, or only the ones initialized by this Lidarr instance.", Advanced = true)]
        public bool Inclusive { get; set; }

        [FieldDefinition(6, Label = "Clean Directories", Type = FieldType.Checkbox, HelpText = "After importing, remove stale directories.", Advanced = true)]
        public bool CleanStaleDirectories { get; set; }

        [FieldDefinition(7, Label = "Corruption Check", Type = FieldType.Checkbox, HelpText = "Scan completed downloads for audio corruption with ffmpeg before Lidarr imports them. Corrupt albums are automatically blocklisted and Lidarr re-searches for a different release. Retries are skipped for corrupt files (the same peer would serve the same bad file). Requires ffmpeg \u2014 configure the path under Settings \u2192 Metadata \u2192 FFmpeg, or have ffmpeg on system PATH.", Advanced = true)]
        public bool CorruptionCheck { get; set; } = true;

        [FieldDefinition(8, Label = "Ban User After Corrupt Count", Type = FieldType.Number, HelpText = "After this many corrupt files from the same user, exclude them from future searches until the plugin restarts. 0 disables the per-user ban.", Advanced = true)]
        public int BanUserAfterCorruptCount { get; set; }

        [FieldDefinition(9, Label = "Max Queue Position Before Cancel", Type = FieldType.Number, HelpText = "Cancel a queued file when the peer's queue position exceeds this value, forcing Lidarr to re-search with a different peer. 0 disables.", Advanced = true)]
        public int MaxQueuePositionBeforeCancel { get; set; } = 100;

        [FieldDefinition(10, Label = "Max Queue Wait", Type = FieldType.Number, Unit = "minutes", HelpText = "Cancel a file that has been queued at a peer for longer than this. 0 disables.", Advanced = true)]
        public int MaxQueueWaitMinutes { get; set; } = 60;

        [FieldDefinition(11, Label = "Stall Timeout", Type = FieldType.Number, Unit = "minutes", HelpText = "Cancel a file that is InProgress but hasn't transferred any new bytes for this long. 0 disables.", Advanced = true)]
        public int StallTimeoutMinutes { get; set; } = 15;

        [FieldDefinition(98, Label = "Is Fetched remote", Type = FieldType.Checkbox, Hidden = HiddenType.Hidden)]
        public bool IsRemotePath { get; set; }

        [FieldDefinition(99, Label = "Host", Type = FieldType.Textbox, Hidden = HiddenType.Hidden)]
        public string Host
        {
            get => _host ??= (HostRegex().Match(BaseUrl) is { Success: true } match) ? match.Groups[1].Value : BaseUrl;
            set { }
        }

        public bool IsLocalhost { get; set; }

        public string DownloadPath { get; set; } = string.Empty;

        public TimeSpan? GetTimeout() => Timeout == null ? null : TimeSpan.FromHours(Timeout.Value);

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));

        [GeneratedRegex(@"^(?:https?:\/\/)?([^\/:\?]+)(?::\d+)?(?:\/|$)", RegexOptions.Compiled)]
        private static partial Regex HostRegex();
    }
}