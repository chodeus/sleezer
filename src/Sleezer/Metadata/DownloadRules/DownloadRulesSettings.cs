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
            HelpText = "Take an album only when one source has all of it. Qobuz fails a download that skips a track, Deezer hides albums with a track it won't serve and never fills a missing FLAC track with MP3, and slskd drops folders with fewer tracks than the release and singles pieced together from several folders. Off lets each source complete an album from partial material.",
            HelpTextWarning = "These rules apply to every Sleezer indexer and download client whether or not this entry is enabled.")]
        public bool WholeAlbumsOnly { get; set; } = true;

        [FieldDefinition(1, Label = "Hide Albums This Account Can't Stream", Type = FieldType.Checkbox,
            HelpText = "Qobuz and Tidal: hide albums the store marks as not streamable for this account, usually licensing gaps in your country.")]
        public bool HideUnstreamable { get; set; } = true;

        [FieldDefinition(2, Label = "Strict Matching", Type = FieldType.Checkbox, HelpText = StrictMatchingHelp)]
        public bool StrictMatching { get; set; } = true;

        [FieldDefinition(3, Label = "Run Pre-Import Tagging On", Type = FieldType.TagSelect, SelectOptions = typeof(PostProcessClient), Placeholder = "Type to add a client",
            HelpText = "Run Lidarr's identification + tag writer on downloaded files before import for the selected Sleezer downloaders, so untagged or mistagged releases match cleanly. Empty = tagging disabled. Not on torrent or Usenet downloads.")]
        public IEnumerable<int> PreImportTaggingClients { get; set; } = Array.Empty<int>();

        [FieldDefinition(4, Label = "Remove Featured Artists From Tags", Type = FieldType.Checkbox,
            HelpText = "Pre-import tagging always ignores '(feat. X)' when matching files for the clients selected above. This also removes it from the title and artist tags of the files it tags, renaming them to match, and removes it, and a '(with X)' naming a credited artist, from the title and album tags of the files it can't match.")]
        public bool StripFeaturedArtists { get; set; }

        // Set once the values from the old per-provider settings have been copied in.
        [FieldDefinition(99, Label = "Values Imported", Type = FieldType.Checkbox, Hidden = HiddenType.Hidden)]
        public bool ValuesImported { get; set; }

        public const string StrictMatchingHelp = "Verify each result's artist and title, plus track count and length where the store reports them, against the MusicBrainz release, and reject remix, live, acoustic, piano, cover, stripped and other alternate versions unless the album itself is one. A result whose title and artist match exactly reaches Lidarr under the searched album's name. Failing results are not hidden: they reach Lidarr carrying the reason, so automatic search skips them while interactive search shows why and still lets you grab one. Various Artists compilations are dropped outright. Also covers the release-year check when a result carries a full release date.";

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}
