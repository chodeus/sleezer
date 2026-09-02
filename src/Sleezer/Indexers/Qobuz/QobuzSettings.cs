using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Validation;
using NzbDrone.Plugin.Sleezer.Core.Model;

namespace NzbDrone.Core.Indexers.Qobuz
{
    public class QobuzIndexerSettingsValidator : AbstractValidator<QobuzIndexerSettings>
    {
        public QobuzIndexerSettingsValidator()
        {
            RuleFor(x => x)
                .Must(x => HasToken(x) || HasEmailPassword(x))
                .WithMessage("Enter either User ID + Auth Token (required for downloading), or Email + MD5 password (search only).");

            RuleFor(x => x.MD5Password)
                .Matches("^[a-fA-F0-9]{32}$")
                .When(x => !string.IsNullOrEmpty(x.MD5Password))
                .WithMessage("Password must be the 32-character MD5 hash of your Qobuz password, not the password itself.");

            // Both or neither — a half-filled override sends one real value and one
            // empty, which is worse than falling back to the bundle.js pair.
            RuleFor(x => x.AppSecret)
                .NotEmpty()
                .When(x => !string.IsNullOrEmpty(x.AppID))
                .WithMessage("App Secret is required when App ID is set.");

            RuleFor(x => x.AppID)
                .NotEmpty()
                .When(x => !string.IsNullOrEmpty(x.AppSecret))
                .WithMessage("App ID is required when App Secret is set.");
        }

        private static bool HasToken(QobuzIndexerSettings x)
            => !string.IsNullOrEmpty(x.UserID) && !string.IsNullOrEmpty(x.UserAuthToken);

        private static bool HasEmailPassword(QobuzIndexerSettings x)
            => !string.IsNullOrEmpty(x.Email) && !string.IsNullOrEmpty(x.MD5Password);
    }

    public class QobuzIndexerSettings : IIndexerSettings, IStoreMatchingSettings
    {
        private static readonly QobuzIndexerSettingsValidator Validator = new();

        [FieldDefinition(0, Label = "User ID", Type = FieldType.Textbox, HelpText = "Your numeric Qobuz user ID. On play.qobuz.com open DevTools → Console and run: JSON.parse(localStorage.localuser).id")]
        public string UserID { get; set; } = "";

        [FieldDefinition(1, Label = "User Auth Token", Type = FieldType.Password, Privacy = PrivacyLevel.ApiKey, HelpText = "The credential that enables downloading. On play.qobuz.com open DevTools → Console and run: copy(JSON.parse(localStorage.localuser).token) — it is then on your clipboard. The Network tab also carries it as the X-User-Auth-Token header, but only on api.qobuz.com calls, so the player has to be doing something first.")]
        public string UserAuthToken { get; set; } = "";

        [FieldDefinition(2, Label = "Qobuz Email", Type = FieldType.Textbox, Advanced = true, HelpText = "Alternative to the token above.", HelpTextWarning = "Email/password sessions can search but CANNOT download — Qobuz refuses getFileUrl on them. Use the token for a working download client.")]
        public string Email { get; set; } = "";

        [FieldDefinition(3, Label = "Qobuz Password (MD5)", Type = FieldType.Password, Advanced = true, Privacy = PrivacyLevel.Password, HelpText = "The MD5 hash of your Qobuz password, not the password itself.")]
        public string MD5Password { get; set; } = "";

        [FieldDefinition(4, Label = "App ID", Type = FieldType.Textbox, Advanced = true, Placeholder = "Auto-detected", HelpText = "Leave blank. Sleezer reads the current App ID from Qobuz's web player; only set this if that ever stops working.")]
        public string AppID { get; set; } = "";

        [FieldDefinition(5, Label = "App Secret", Type = FieldType.Password, Advanced = true, Privacy = PrivacyLevel.ApiKey, Placeholder = "Auto-detected", HelpText = "Leave blank. Set only alongside a manual App ID.")]
        public string AppSecret { get; set; } = "";

        [FieldDefinition(6, Label = "Hide Non-Streamable Releases", Type = FieldType.Checkbox, HelpText = "Skip albums Qobuz marks as not streamable for your account — usually licensing gaps in your country.")]
        public bool HideNonStreamable { get; set; } = true;

        [FieldDefinition(7, Type = FieldType.Number, Label = "Early Download Limit", Unit = "days", HelpText = "Time before release date Lidarr will download from this indexer, empty is no limit", Advanced = true)]
        public int? EarlyReleaseLimit { get; set; }

        [FieldDefinition(8, Label = "Strict Matching", Type = FieldType.Checkbox, HelpText = "Verify each result's artist, title, track count and length against the MusicBrainz release, and reject remix, live, acoustic and extended variants unless the album itself is one. Interactive search still shows everything.")]
        public bool StrictMatching { get; set; } = true;

        // Hardcoded to the Qobuz API host; only present because IIndexerSettings demands it.
        public string BaseUrl { get; set; } = "";

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}
