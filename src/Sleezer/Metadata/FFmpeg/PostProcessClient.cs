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
        /// <summary>True when the client is a storefront, so its product can only be digital.</summary>
        public static bool IsDigitalStorefront(this PostProcessClient client) => client switch
        {
            PostProcessClient.Qobuz or
            PostProcessClient.Tidal or
            PostProcessClient.Deezer or
            PostProcessClient.Bandcamp or
            PostProcessClient.Lucida or
            PostProcessClient.DABMusic or
            PostProcessClient.TripleTriple => true,

            // Fail closed: a client added later must be classified deliberately. Slskd and
            // SubSonic can both serve a genuine CD rip.
            _ => false
        };
    }
}
