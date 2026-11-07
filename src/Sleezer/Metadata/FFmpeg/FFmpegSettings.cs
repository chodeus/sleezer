using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;
using NzbDrone.Plugin.Sleezer.Core.Model;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
// Aliased so `XabeFFmpeg` can't be shadowed by our local Metadata.FFmpeg namespace.
using XabeFFmpeg = Xabe.FFmpeg.FFmpeg;

namespace NzbDrone.Plugin.Sleezer.Metadata.FFmpeg
{
    public class FFmpegSettingsValidator : AbstractValidator<FFmpegSettings>
    {
        public FFmpegSettingsValidator()
        {
            // Validate FFmpegPath
            RuleFor(x => x.FFmpegPath)
                .NotEmpty()
                .WithMessage("FFmpeg path is required.")
                .MustAsync(async (ffmpegPath, cancellationToken) => await TestFFmpeg(ffmpegPath))
                .WithMessage("FFmpeg is not installed or invalid at the specified path.");

            // Validate custom conversion rules
            RuleFor(x => x.CustomConversion)
                .Must(customConversions => customConversions?.All(IsValidConversionRule) != false)
                .WithMessage("Custom conversion rules must be in the format 'source -> target' (e.g., mp3 to flac).");

            RuleFor(x => x.CustomConversion)
                .Must(customConversions => customConversions?.All(IsValidLossyConversion) != false)
                .WithMessage("Lossy formats cannot be converted to non-lossy formats.");

            RuleFor(x => x.CustomConversion)
                .Must(customConversions => customConversions?.All(IsValidCBRUsage) != false)
                .WithMessage("CBR flag is only applicable to lossy formats. Lossless formats are inherently variable bitrate.");

            RuleFor(x => x)
                .Must(settings => IsValidStaticConversion(settings))
                .WithMessage("Lossy formats cannot be converted to non-lossy formats.");
        }

        private bool IsValidConversionRule(KeyValuePair<string, string> rule)
        {
            if (string.IsNullOrWhiteSpace(rule.Key) || string.IsNullOrWhiteSpace(rule.Value))
                return false;

            return RuleParser.TryParseRule(rule.Key, rule.Value, out _);
        }

        private bool IsValidLossyConversion(KeyValuePair<string, string> rule)
        {
            if (!RuleParser.TryParseRule(rule.Key, rule.Value, out ConversionRule parsedRule))
                return false;

            if (parsedRule.IsGlobalRule)
                return true;

            if (AudioFormatHelper.IsLossyFormat(parsedRule.SourceFormat) &&
                !AudioFormatHelper.IsLossyFormat(parsedRule.TargetFormat))
                return false;

            return true;
        }


        private bool IsValidCBRUsage(KeyValuePair<string, string> rule)
        {
            if (!RuleParser.TryParseRule(rule.Key, rule.Value, out ConversionRule parsedRule))
                return false;

            // If CBR is specified, target must be a lossy format
            if (parsedRule.UseCBR && !AudioFormatHelper.IsLossyFormat(parsedRule.TargetFormat))
                return false;

            return true;
        }

        private static bool IsValidStaticConversion(FFmpegSettings settings) =>
            AudioFormatHelper.IsLossyFormat((AudioFormat)settings.TargetFormat) || (!settings.ConvertMP3 && !settings.ConvertAAC && !settings.ConvertOpus && !settings.ConvertOther);

        private static async Task<bool> TestFFmpeg(string ffmpegPath)
        {
            if (string.IsNullOrWhiteSpace(ffmpegPath))
                return false;

            string oldPath = XabeFFmpeg.ExecutablesPath;
            XabeFFmpeg.SetExecutablesPath(ffmpegPath);
            AudioMetadataHandler.ResetFFmpegInstallationCheck();

            if (!AudioMetadataHandler.CheckFFmpegInstalled())
            {
                try
                {
                    await AudioMetadataHandler.InstallFFmpeg(ffmpegPath);
                }
                catch (Exception ex)
                {
                    // Log so the user has something to look at if Test fails — the
                    // validator only surfaces a generic "not installed or invalid"
                    // error to the UI, but the underlying TimeoutException /
                    // HttpRequestException / extraction failure goes here.
                    NzbDrone.Common.Instrumentation.NzbDroneLogger.GetLogger(typeof(FFmpegSettingsValidator))
                        .Warn(ex, "FFmpeg auto-install failed for path {Path}", ffmpegPath);
                    if (!string.IsNullOrEmpty(oldPath))
                        XabeFFmpeg.SetExecutablesPath(oldPath);
                    return false;
                }
            }
            return true;
        }
    }

    public class FFmpegSettings : IProviderConfig
    {
        private static readonly FFmpegSettingsValidator Validator = new();

        private IEnumerable<KeyValuePair<string, string>> _customConversion = [];

        [FieldDefinition(0, Label = "FFmpeg Path", Type = FieldType.Path, Section = MetadataSectionType.Metadata, Placeholder = "/downloads/FFmpeg", HelpText = "Required. Where ffmpeg is kept; saving installs it if missing. A newer ffmpeg on the PATH is used instead.", HelpTextWarning = "Enable only turns on conversion, which runs on every import, torrent and Usenet included. The corrupt scan runs either way.")]
        public string FFmpegPath { get; set; } = string.Empty;

        [FieldDefinition(1, Label = "Convert MP3", Type = FieldType.Checkbox, Section = MetadataSectionType.Metadata, HelpText = "Convert MP3 files.")]
        public bool ConvertMP3 { get; set; }

        [FieldDefinition(2, Label = "Convert AAC", Type = FieldType.Checkbox, Section = MetadataSectionType.Metadata, HelpText = "Convert AAC files.")]
        public bool ConvertAAC { get; set; }

        [FieldDefinition(3, Label = "Convert FLAC", Type = FieldType.Checkbox, Section = MetadataSectionType.Metadata, HelpText = "Convert FLAC files.")]
        public bool ConvertFLAC { get; set; }

        [FieldDefinition(4, Label = "Convert WAV", Type = FieldType.Checkbox, Section = MetadataSectionType.Metadata, HelpText = "Convert WAV files.")]
        public bool ConvertWAV { get; set; }

        [FieldDefinition(5, Label = "Convert Opus", Type = FieldType.Checkbox, Section = MetadataSectionType.Metadata, HelpText = "Convert Opus files.")]
        public bool ConvertOpus { get; set; }

        [FieldDefinition(7, Label = "Convert Other Formats", Type = FieldType.Checkbox, Section = MetadataSectionType.Metadata, HelpText = "Convert other formats (e.g., WMA).")]
        public bool ConvertOther { get; set; }

        [FieldDefinition(8, Label = "Target Format", Type = FieldType.Select, SelectOptions = typeof(TargetAudioFormat), Section = MetadataSectionType.Metadata, HelpText = "Select the target format to convert audio files into.")]
        public int TargetFormat { get; set; } = (int)TargetAudioFormat.Opus;

        // Never null: an API save can send null, and the converter reads this on every import.
        [FieldDefinition(9, Label = "Custom Conversion Rules", Type = FieldType.KeyValueList, Section = MetadataSectionType.Metadata, HelpText = "Rules such as 'flac -> mp3:320:cbr' or 'flac:24 -> flac:16'. Upsampling is blocked.")]
        public IEnumerable<KeyValuePair<string, string>> CustomConversion
        {
            get => _customConversion;
            set => _customConversion = value ?? [];
        }

        [FieldDefinition(10, Label = "Run Corrupt Scan On", Type = FieldType.TagSelect, SelectOptions = typeof(PostProcessClient), Section = MetadataSectionType.Metadata, Placeholder = "Type to add a client", HelpText = "Check downloads from these clients for corrupt files; one fails the download and it is searched again. Empty = off.")]
        public IEnumerable<int> CorruptionScanClients { get; set; } = Array.Empty<int>();

        // Moved to the Tidal download client; kept hidden so the one-time copy can read them.
        [FieldDefinition(13, Label = "Tidal: Extract FLAC From M4A", Type = FieldType.Checkbox, Hidden = HiddenType.Hidden)]
        public bool TidalExtractFlac { get; set; }

        [FieldDefinition(14, Label = "Tidal: Re-encode AAC Into MP3", Type = FieldType.Checkbox, Hidden = HiddenType.Hidden)]
        public bool TidalReEncodeAAC { get; set; }

        [FieldDefinition(15, Label = "Tidal Options Moved", Type = FieldType.Checkbox, Hidden = HiddenType.Hidden)]
        public bool TidalOptionsMoved { get; set; }

        // Moved to Sleezer Download Rules; kept hidden so the one-time copy can read them.
        [FieldDefinition(11, Label = "Run Pre-Import Tagging On", Type = FieldType.TagSelect, SelectOptions = typeof(PostProcessClient), Hidden = HiddenType.Hidden)]
        public IEnumerable<int> PreImportTaggingClients { get; set; } = Array.Empty<int>();

        [FieldDefinition(12, Label = "Remove Featured Artists From Tags", Type = FieldType.Checkbox, Hidden = HiddenType.Hidden)]
        public bool StripFeaturedArtists { get; set; }

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }

    public enum TargetAudioFormat
    {
        [FieldOption(Label = "AAC", Hint = "Convert to AAC format.")]
        AAC = 1,

        [FieldOption(Label = "MP3", Hint = "Convert to MP3 format.")]
        MP3 = 2,

        [FieldOption(Label = "Opus", Hint = "Convert to Opus format.")]
        Opus = 3,

        [FieldOption(Label = "FLAC", Hint = "Convert to FLAC format.")]
        FLAC = 5,
    }
}
