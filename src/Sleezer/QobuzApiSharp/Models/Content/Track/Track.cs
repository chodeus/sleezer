// Vendored from DaveBinM/QobuzApiSharp (GPL-3.0), kept structurally as-is so
// upstream fixes can be pulled by hand. Nullable is off for the same reason.
#nullable disable
using Newtonsoft.Json;
using System;

namespace QobuzApiSharp.Models.Content
{
    public class Track : MostPopularContent
    {
        [JsonProperty("album")]
        public Album Album { get; set; }

        [JsonProperty("audio_info")]
        public AudioInfo AudioInfo { get; set; }

        [JsonProperty("composer")]
        public Artist Composer { get; set; }

        [JsonProperty("copyright")]
        public string Copyright { get; set; }

        [JsonProperty("displayable")]
        public bool? Displayable { get; set; }

        [JsonProperty("downloadable")]
        public bool? Downloadable { get; set; }

        [JsonProperty("duration")]
        public long? Duration { get; set; }

        [JsonProperty("hires")]
        public bool? Hires { get; set; }

        [JsonProperty("hires_streamable")]
        public bool? HiresStreamable { get; set; }

        [JsonProperty("id")]
        public int? Id { get; set; }

        [JsonProperty("isrc")]
        public string Isrc { get; set; }

        [JsonProperty("maximum_bit_depth")]
        public double? MaximumBitDepth { get; set; }

        [JsonProperty("maximum_channel_count")]
        public double? MaximumChannelCount { get; set; }

        [JsonProperty("maximum_sampling_rate")]
        public double? MaximumSamplingRate { get; set; }

        [JsonProperty("media_number")]
        public int? MediaNumber { get; set; }

        [JsonProperty("parental_warning")]
        public bool? ParentalWarning { get; set; }

        [JsonProperty("performer")]
        public Artist Performer { get; set; }

        [JsonProperty("performers")]
        public string Performers { get; set; }

        [JsonProperty("previewable")]
        public bool? Previewable { get; set; }

        [JsonProperty("purchasable")]
        public bool? Purchasable { get; set; }

        [JsonProperty("purchasable_at")]
        public long? PurchasableAt { get; set; }

        [JsonProperty("release_date_download")]
        public DateTimeOffset? ReleaseDateDownload { get; set; }

        [JsonProperty("release_date_original")]
        public DateTimeOffset? ReleaseDateOriginal { get; set; }

        [JsonProperty("release_date_stream")]
        public DateTimeOffset? ReleaseDateStream { get; set; }

        [JsonProperty("sampleable")]
        public bool? Sampleable { get; set; }

        [JsonProperty("streamable")]
        public bool? Streamable { get; set; }

        [JsonProperty("streamable_at")]
        public long? StreamableAt { get; set; }

        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("track_number")]
        public int? TrackNumber { get; set; }

        [JsonProperty("version")]
        public string Version { get; set; }

        [JsonProperty("work")]
        public string Work { get; set; }

        public string CompleteTitle
        {
            get
            {
                var title = Title.Trim();

                if (title.EndsWith(" (Explicit)"))
                    title = title[..title.IndexOf(" (Explicit)")].Trim();

                if (!string.IsNullOrEmpty(Version))
                    title = $"{title} ({Version})";

                // Is this the correct format? I am not sure.
                // This is purely based on https://www.qobuz.com/us-en/album/the-world-of-hans-zimmer-part-ii-a-new-dimension-hans-zimmer/py9kvu4v2arvb
                if (!string.IsNullOrEmpty(Work))
                    title = $"{Work}: {title}";

                return title;
            }
        }
    }
}
