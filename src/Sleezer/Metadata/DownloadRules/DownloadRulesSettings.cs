using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;
using NzbDrone.Plugin.Sleezer.Metadata.FFmpeg;

namespace NzbDrone.Plugin.Sleezer.Metadata.DownloadRules
{
    public class DownloadRulesSettings : IProviderConfig
    {
        private static readonly InlineValidator<DownloadRulesSettings> Validator = new();

        [FieldDefinition(0, Label = "Whole Albums Only", Type = FieldType.Checkbox,
            HelpText = "Take an album only when one source has every track. Off allows partial albums.",
            HelpTextWarning = "These rules apply to every Sleezer indexer and download client whether or not this entry is enabled.")]
        public bool WholeAlbumsOnly { get; set; } = true;

        [FieldDefinition(1, Label = "Hide Albums This Account Can't Stream", Type = FieldType.Checkbox,
            HelpText = "Qobuz and Tidal: hide albums this account can't stream, usually because of licensing.")]
        public bool HideUnstreamable { get; set; } = true;

        [FieldDefinition(2, Label = "Strict Matching", Type = FieldType.Checkbox, HelpText = StrictMatchingHelp)]
        public bool StrictMatching { get; set; } = true;

        [FieldDefinition(3, Label = "Run Pre-Import Tagging On", Type = FieldType.TagSelect, SelectOptions = typeof(PostProcessClient), Placeholder = "Type to add a client",
            HelpText = "Identify and tag downloads from these Sleezer clients before import. Empty = off.")]
        public IEnumerable<int> PreImportTaggingClients { get; set; } = Array.Empty<int>();

        [FieldDefinition(4, Label = "Remove Featured Artists From Tags", Type = FieldType.Checkbox,
            HelpText = "Remove '(feat. X)' credits from the tags pre-import tagging writes.")]
        public bool StripFeaturedArtists { get; set; }

        // Set once the values from the old per-provider settings have been copied in.
        [FieldDefinition(99, Label = "Values Imported", Type = FieldType.Checkbox, Hidden = HiddenType.Hidden)]
        public bool ValuesImported { get; set; }

        public const string StrictMatchingHelp = "Check results against the release and reject mismatches and alternate versions such as live or remix; interactive search still lists them with the reason.";

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}
