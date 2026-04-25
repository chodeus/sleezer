using System;
using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Indexers.Tidal
{
    public class TidalIndexerSettingsValidator : AbstractValidator<TidalIndexerSettings>
    {
        public TidalIndexerSettingsValidator()
        {
            RuleFor(c => c.AccessToken).NotEmpty().WithMessage("Authenticate with Tidal to populate this field.");
            RuleFor(c => c.RefreshToken).NotEmpty().WithMessage("Authenticate with Tidal to populate this field.");
        }
    }

    public class TidalIndexerSettings : IIndexerSettings
    {
        private static readonly TidalIndexerSettingsValidator Validator = new();

        public TidalIndexerSettings()
        {
            SignIn = "startOAuth";
        }

        // Hidden token storage populated by the FieldType.OAuth flow's getOAuthToken
        // callback. Same shape as Spotify's import-list settings — Lidarr's UI sets
        // these from the dictionary returned by RequestAction("getOAuthToken").
        [FieldDefinition(0, Label = "Access Token", Type = FieldType.Textbox, Hidden = HiddenType.Hidden)]
        public string AccessToken { get; set; } = "";

        [FieldDefinition(1, Label = "Refresh Token", Type = FieldType.Textbox, Hidden = HiddenType.Hidden)]
        public string RefreshToken { get; set; } = "";

        [FieldDefinition(2, Label = "Token Type", Type = FieldType.Textbox, Hidden = HiddenType.Hidden)]
        public string TokenType { get; set; } = "Bearer";

        [FieldDefinition(3, Label = "Expires", Type = FieldType.Textbox, Hidden = HiddenType.Hidden)]
        public DateTime Expires { get; set; }

        [FieldDefinition(4, Label = "Country Code", Type = FieldType.Textbox, Hidden = HiddenType.Hidden)]
        public string CountryCode { get; set; } = "";

        [FieldDefinition(5, Label = "User Id", Type = FieldType.Number, Hidden = HiddenType.Hidden)]
        public long UserId { get; set; }

        [FieldDefinition(10, Label = "Hide Albums With Missing Tracks", HelpText = "If an album has any unavailable tracks on Tidal, they will not be provided when searching.", Type = FieldType.Checkbox)]
        public bool HideAlbumsWithMissing { get; set; } = true;

        [FieldDefinition(11, Label = "Hide Clean Releases", HelpText = "Skip albums labelled as 'Clean'. Non-clean releases are tagged [Explicit] in the title so you can filter with release profiles.", Type = FieldType.Checkbox)]
        public bool HideCleanReleases { get; set; } = true;

        [FieldDefinition(12, Type = FieldType.Number, Label = "Early Download Limit", Unit = "days", HelpText = "Time before release date Lidarr will download from this indexer, empty is no limit", Advanced = true)]
        public int? EarlyReleaseLimit { get; set; }

        [FieldDefinition(99, Label = "Authenticate with Tidal", Type = FieldType.OAuth)]
        public string SignIn { get; set; }

        public string BaseUrl { get; set; } = "";

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
