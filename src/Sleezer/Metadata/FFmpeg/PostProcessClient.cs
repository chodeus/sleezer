using NzbDrone.Core.Annotations;

namespace NzbDrone.Plugin.Sleezer.Metadata.FFmpeg
{
    public enum PostProcessClient
    {
        [FieldOption(Label = "Deezer")]
        Deezer = 1,

        [FieldOption(Label = "Tidal")]
        Tidal = 2,

        [FieldOption(Label = "Slskd (Soulseek)")]
        Slskd = 3,

        [FieldOption(Label = "Qobuz")]
        Qobuz = 4,

        [FieldOption(Label = "Bandcamp")]
        Bandcamp = 5,

        [FieldOption(Label = "Lucida")]
        Lucida = 6,

        [FieldOption(Label = "DABMusic")]
        DABMusic = 7,

        [FieldOption(Label = "TripleTriple")]
        TripleTriple = 8,

        [FieldOption(Label = "SubSonic")]
        SubSonic = 9,
    }

    public static class PostProcessClientExtensions
    {
        /// <summary>
        /// True when the client is a digital storefront, so whatever it delivers can only be
        /// a digital release.
        /// </summary>
        public static bool IsDigitalStorefront(this PostProcessClient client) => client switch
        {
            PostProcessClient.Qobuz or
            PostProcessClient.Tidal or
            PostProcessClient.Deezer or
            PostProcessClient.Bandcamp or
            PostProcessClient.Lucida or
            PostProcessClient.DABMusic or
            PostProcessClient.TripleTriple => true,

            // Slskd is peer-to-peer and SubSonic is a personal library — either can serve a
            // CD rip, so steering them onto a digital release is the same error reversed.
            // Unlisted defaults to false: a new peer source must opt in, not inherit this.
            _ => false
        };
    }
}
