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

        [FieldDefinition(0, Label = "FFmpeg Path", Type = FieldType.Path, Section = MetadataSectionType.Metadata, Placeholder = "/downloads/FFmpeg", HelpText = "Required. Where Sleezer keeps ffmpeg for conversion and for the corrupt scan's decode check. Saving installs ffmpeg here if it is missing (from chodeus/ffmpeg-static, checked for updates daily). A newer ffmpeg on the host PATH is preferred over the downloaded copy.", HelpTextWarning = "Enable only switches on conversion, and conversion runs on EVERY track Lidarr imports, torrent and Usenet included. The corrupt scan and pre-import tagging below run on the Sleezer downloaders you pick, whether or not this entry is enabled.")]
        public string FFmpegPath { get; set; } = string.Empty;

        [FieldDefinition(1, Label = "Convert MP3", Type = FieldType.Checkbox, Section = MetadataSectionType.Metadata, HelpText = "Convert MP3 files. Applies to all imports (torrent/Usenet/plugin) when this provider is enabled.")]
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

        [FieldDefinition(9, Label = "Custom Conversion Rules", Type = FieldType.KeyValueList, Section = MetadataSectionType.Metadata, HelpText = "Custom conversion rules. Examples: 'flac -> mp3:320:cbr' (FLAC to CBR MP3), 'mp3:320 -> mp3:128' (downsample), 'flac:24 -> flac:16' (reduce bit depth). Add ':cbr' for constant bitrate encoding. Upsampling is blocked automatically.")]
        public IEnumerable<KeyValuePair<string, string>> CustomConversion { get; set; } = [];

        [FieldDefinition(10, Label = "Run Corrupt Scan On", Type = FieldType.TagSelect, SelectOptions = typeof(PostProcessClient), Section = MetadataSectionType.Metadata, Placeholder = "Type to add a client", HelpText = "After download, scan audio files for corruption on the selected Sleezer downloaders: size, TagLib parse, and a decode with the ffmpeg at FFmpeg Path above. Corrupt files are quarantined and the release is re-searched. Empty = scan disabled. Runs whether or not this entry is enabled; not on torrent or Usenet downloads.")]
        public IEnumerable<int> CorruptionScanClients { get; set; } = Array.Empty<int>();

        [FieldDefinition(11, Label = "Run Pre-Import Tagging On", Type = FieldType.TagSelect, SelectOptions = typeof(PostProcessClient), Section = MetadataSectionType.Metadata, Placeholder = "Type to add a client", HelpText = "Run Lidarr's identification + tag writer on downloaded files before import for the selected Sleezer downloaders, so untagged or mistagged releases match cleanly. Does not use ffmpeg. Empty = tagging disabled. Runs whether or not this entry is enabled; not on torrent or Usenet downloads.")]
        public IEnumerable<int> PreImportTaggingClients { get; set; } = Array.Empty<int>();

        [FieldDefinition(12, Label = "Remove Featured Artists From Tags", Type = FieldType.Checkbox, Section = MetadataSectionType.Metadata, HelpText = "Pre-import tagging always ignores '(feat. X)' / '(featuring Y)' / '(ft Z)' when matching files for the clients selected above. This also removes them from the written title and artist tags, and renames files to match.")]
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
