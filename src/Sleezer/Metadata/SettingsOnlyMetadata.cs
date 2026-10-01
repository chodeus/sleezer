using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Extras.Metadata.Files;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using NzbDrone.Core.ThingiProvider;

namespace NzbDrone.Plugin.Sleezer.Metadata
{
    /// <summary>A Metadata entry that only holds settings for the rest of Sleezer; it writes no files.</summary>
    public abstract class SettingsOnlyMetadata<TSettings> : MetadataBase<TSettings>
        where TSettings : IProviderConfig, new()
    {
        public override MetadataFile FindMetadataFile(Artist artist, string path) => default!;

        public override MetadataFileResult ArtistMetadata(Artist artist) => default!;

        public override MetadataFileResult AlbumMetadata(Artist artist, Album album, string albumPath) => default!;

        public override MetadataFileResult TrackMetadata(Artist artist, TrackFile trackFile) => default!;

        // MetadataService iterates these without a null check, unlike the three above.
        public override List<ImageFileResult> ArtistImages(Artist artist) => [];

        public override List<ImageFileResult> AlbumImages(Artist artist, Album album, string albumFolder) => [];

        public override List<ImageFileResult> TrackImages(Artist artist, TrackFile trackFile) => [];
    }
}
