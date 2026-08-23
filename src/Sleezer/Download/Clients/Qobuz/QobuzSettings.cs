using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;

namespace NzbDrone.Core.Download.Clients.Qobuz
{
    public class QobuzSettingsValidator : AbstractValidator<QobuzSettings>
    {
        public QobuzSettingsValidator()
        {
            RuleFor(x => x.DownloadPath).IsValidPath();

            RuleFor(x => x.MaxConcurrentTracks)
                .InclusiveBetween(1, 8)
                .WithMessage("Max concurrent tracks must be between 1 and 8.");

            RuleFor(x => x.CustomArtworkResolution)
                .InclusiveBetween(100, 4000)
                .When(x => x.ArtworkSize == (int)QobuzArtworkSize.Custom)
                .WithMessage("Custom artwork resolution must be between 100 and 4000 pixels.");
        }
    }

    public class QobuzSettings : IProviderConfig
    {
        private static readonly QobuzSettingsValidator Validator = new();

        [FieldDefinition(0, Label = "Download Path", Type = FieldType.Textbox)]
        public string DownloadPath { get; set; } = "";

        [FieldDefinition(1, Label = "Require Complete Album", Type = FieldType.Checkbox, HelpText = "Fail the whole album if any track can't be downloaded, instead of importing it with tracks missing. Recommended — it lets Lidarr retry or pick another release.")]
        public bool RequireCompleteAlbum { get; set; } = true;

        [FieldDefinition(2, Label = "Save Synced Lyrics", Type = FieldType.Checkbox, HelpText = "Saves synced lyrics to a separate .lrc file if available. Requires .lrc to be allowed under Import Extra Files.")]
        public bool SaveSyncedLyrics { get; set; }

        [FieldDefinition(3, Label = "Use LRCLIB as Lyric Provider", Type = FieldType.Checkbox, HelpText = "Qobuz supplies no lyrics of its own; this fetches them from LRCLIB instead.")]
        public bool UseLRCLIB { get; set; }

        [FieldDefinition(4, Label = "Artwork Size", Type = FieldType.Select, SelectOptions = typeof(QobuzArtworkSize), HelpText = "Cover resolution embedded in tracks and written as a sidecar. 'Custom' downscales Qobuz's original.")]
        public int ArtworkSize { get; set; } = (int)QobuzArtworkSize.Large;

        [FieldDefinition(5, Label = "Custom Artwork Resolution", Type = FieldType.Number, Advanced = true, Unit = "px", HelpText = "Used only when Artwork Size is Custom: the original cover is downscaled to fit within this many pixels.")]
        public int CustomArtworkResolution { get; set; } = 1000;

        [FieldDefinition(6, Label = "Artwork Placement", Type = FieldType.Select, SelectOptions = typeof(QobuzArtworkPlacement), HelpText = "Embed the cover in each track, write a cover.jpg beside them, or both.")]
        public int ArtworkPlacement { get; set; } = (int)QobuzArtworkPlacement.Embed;

        [FieldDefinition(7, Label = "Max Concurrent Tracks", Type = FieldType.Number, Advanced = true, HelpText = "Tracks downloaded in parallel per album. Raising this makes Qobuz rate-limit sooner.")]
        public int MaxConcurrentTracks { get; set; } = 3;

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }

    public enum QobuzArtworkSize
    {
        [FieldOption(Label = "Small (230px)")]
        Small = 0,

        [FieldOption(Label = "Large (600px)")]
        Large = 1,

        [FieldOption(Label = "Original (max)")]
        Original = 2,

        [FieldOption(Label = "Custom (downscale)")]
        Custom = 3
    }

    public enum QobuzArtworkPlacement
    {
        [FieldOption(Label = "Embed in tracks")]
        Embed = 0,

        [FieldOption(Label = "Sidecar (cover.jpg)")]
        Sidecar = 1,

        [FieldOption(Label = "Both")]
        Both = 2
    }
}
