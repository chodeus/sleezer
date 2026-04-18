using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Validation;

namespace NzbDrone.Plugin.Sleezer.Indexers.TripleTriple
{
    public class TripleTripleIndexerSettingsValidator : AbstractValidator<TripleTripleIndexerSettings>
    {
        public TripleTripleIndexerSettingsValidator()
        {
            RuleFor(x => x.BaseUrl)
                .NotEmpty().WithMessage("Base URL is required.")
                .Must(url => Uri.IsWellFormedUriString(url, UriKind.Absolute))
                .WithMessage("Base URL must be a valid URL.");

            RuleFor(x => x.CountryCode)
                .Must(cc => Enum.IsDefined(typeof(TripleTripleCountry), cc))
                .WithMessage("Invalid country code selected.");

            RuleFor(x => x.Codec)
                .Must(c => Enum.IsDefined(typeof(TripleTripleCodec), c))
                .WithMessage("Invalid codec selected.");

            RuleFor(x => x.SearchLimit)
                .InclusiveBetween(1, 100).WithMessage("Search limit must be between 1 and 100.");

            RuleFor(x => x.RequestTimeout)
                .InclusiveBetween(10, 300).WithMessage("Request timeout must be between 10 and 300 seconds.");
        }
    }

    public class TripleTripleIndexerSettings : IIndexerSettings
    {
        private static readonly TripleTripleIndexerSettingsValidator _validator = new();

        public TripleTripleIndexerSettings()
        {
            SearchLimit = 50;
            RequestTimeout = 60;
        }

        [FieldDefinition(0, Label = "Base URL", Type = FieldType.Textbox, HelpText = "URL of the T2Tunes API instance", Placeholder = "https://T2Tunes.site")]
        public string BaseUrl { get; set; } = string.Empty;

        [FieldDefinition(1, Label = "Country", Type = FieldType.Select, SelectOptions = typeof(TripleTripleCountry), HelpText = "Country code for Amazon Music region")]
        public int CountryCode { get; set; } = (int)TripleTripleCountry.US;

        [FieldDefinition(2, Label = "Preferred Codec", Type = FieldType.Select, SelectOptions = typeof(TripleTripleCodec), HelpText = "Audio codec preference for downloads")]
        public int Codec { get; set; } = (int)TripleTripleCodec.FLAC;

        [FieldDefinition(7, Label = "Search Limit", Type = FieldType.Number, HelpText = "Maximum number of results to return per search", Hidden = HiddenType.Hidden, Advanced = true)]
        public int SearchLimit { get; set; }

        [FieldDefinition(8, Type = FieldType.Number, Label = "Request Timeout", Unit = "seconds", HelpText = "Timeout for requests to T2Tunes API", Advanced = true)]
        public int RequestTimeout { get; set; }

        [FieldDefinition(9, Type = FieldType.Number, Label = "Early Download Limit", Unit = "days", HelpText = "Time before release date Lidarr will download from this indexer, empty is no limit", Advanced = true)]
        public int? EarlyReleaseLimit { get; set; }

        public NzbDroneValidationResult Validate() => new(_validator.Validate(this));
    }

    public enum TripleTripleCountry
    {
        [FieldOption(Label = "Argentina")]
        AR = 1,
        [FieldOption(Label = "Australia")]
        AU = 2,
        [FieldOption(Label = "Brazil")]
        BR = 3,
        [FieldOption(Label = "Canada")]
        CA = 4,
        [FieldOption(Label = "Costa Rica")]
        CR = 5,
        [FieldOption(Label = "Germany")]
        DE = 6,
        [FieldOption(Label = "Spain")]
        ES = 7,
        [FieldOption(Label = "France")]
        FR = 8,
        [FieldOption(Label = "United Kingdom")]
        GB = 9,
        [FieldOption(Label = "India")]
        IN = 10,
        [FieldOption(Label = "Italy")]
        IT = 11,
        [FieldOption(Label = "Japan")]
        JP = 12,
        [FieldOption(Label = "Mexico")]
        MX = 13,
        [FieldOption(Label = "New Zealand")]
        NZ = 14,
        [FieldOption(Label = "Portugal")]
        PT = 15,
        [FieldOption(Label = "United States")]
        US = 16
    }

    public enum TripleTripleCodec
    {
        [FieldOption(Label = "FLAC (up to 24bit/192kHz)", Hint = "Lossless FLAC format")]
        FLAC = 1,

        [FieldOption(Label = "Opus (320kbps)", Hint = "High quality Opus codec")]
        OPUS = 2,

        [FieldOption(Label = "Dolby Digital Plus (E-AC-3)", Hint = "Dolby Digital Plus codec")]
        EAC3 = 3
    }
}
