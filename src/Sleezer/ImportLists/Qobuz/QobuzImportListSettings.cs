using System;
using System.Collections.Generic;
using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.ImportLists.Qobuz
{
    public class QobuzFavouritesSettings : IImportListSettings
    {
        // Hardcoded to the Qobuz API host; present only because the interface demands it.
        public string BaseUrl { get; set; } = "https://www.qobuz.com";

        public NzbDroneValidationResult Validate() => new();
    }

    public class QobuzPlaylistSettingsValidator : AbstractValidator<QobuzPlaylistSettings>
    {
        public QobuzPlaylistSettingsValidator()
        {
            RuleFor(c => c.PlaylistIds).NotEmpty().WithMessage("Add at least one playlist ID.");
        }
    }

    public class QobuzPlaylistSettings : IImportListSettings
    {
        private static readonly QobuzPlaylistSettingsValidator Validator = new();

        public string BaseUrl { get; set; } = "https://www.qobuz.com";

        [FieldDefinition(0, Label = "Playlist IDs", Type = FieldType.Tag, HelpText = "Qobuz playlist IDs to import from. The ID is the number at the end of a playlist's URL.")]
        public IEnumerable<string> PlaylistIds { get; set; } = Array.Empty<string>();

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}
