using Newtonsoft.Json;

namespace NzbDrone.Core.Indexers.Tidal;

#pragma warning disable CS8618
public partial class TidalSearchResponse
{
    [JsonProperty("artists")]
    public Artists ArtistResults { get; set; }

    [JsonProperty("albums")]
    public Albums AlbumResults { get; set; }

    [JsonProperty("tracks")]
    public Tracks TrackResults { get; set; }

    public class Albums
    {
        [JsonProperty("limit")]
        public long Limit { get; set; }

        [JsonProperty("offset")]
        public long Offset { get; set; }

        [JsonProperty("totalNumberOfItems")]
        public long TotalNumberOfItems { get; set; }

        [JsonProperty("items")]
        public Album[] Items { get; set; }
    }

    public class Album
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("duration")]
        public long Duration { get; set; }

        [JsonProperty("streamReady")]
        public bool StreamReady { get; set; }

        [JsonProperty("djReady")]
        public bool DjReady { get; set; }

        [JsonProperty("stemReady")]
        public bool StemReady { get; set; }

        [JsonProperty("streamStartDate")]
        public string StreamStartDate { get; set; }

        [JsonProperty("allowStreaming")]
        public bool AllowStreaming { get; set; }

        [JsonProperty("premiumStreamingOnly")]
        public bool PremiumStreamingOnly { get; set; }

        [JsonProperty("numberOfTracks")]
        public long NumberOfTracks { get; set; }

        [JsonProperty("numberOfVideos")]
        public long NumberOfVideos { get; set; }

        [JsonProperty("numberOfVolumes")]
        public long NumberOfVolumes { get; set; }

        [JsonProperty("releaseDate")]
        public string ReleaseDate { get; set; }

        [JsonProperty("copyright")]
        public string Copyright { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("url")]
        public string Url { get; set; }

        [JsonProperty("cover")]
        public string Cover { get; set; }

        [JsonProperty("explicit")]
        public bool Explicit { get; set; }

        [JsonProperty("audioQuality")]
        public string AudioQuality { get; set; }

        [JsonProperty("audioModes")]
        public string[] AudioModes { get; set; }

        [JsonProperty("artists")]
        public Artist[] Artists { get; set; }

        [JsonProperty("mediaMetadata")]
        public MediaMetadataData MediaMetadata { get; set; }

        public class MediaMetadataData
        {
            [JsonProperty("tags")]
            public string[] Tags { get; set; }
        }
    }

    public class Artists
    {
        [JsonProperty("limit")]
        public long Limit { get; set; }

        [JsonProperty("offset")]
        public long Offset { get; set; }

        [JsonProperty("totalNumberOfItems")]
        public long TotalNumberOfItems { get; set; }

        [JsonProperty("items")]
        public Artist[] Items { get; set; }
    }

    public class Tracks
    {
        [JsonProperty("limit")]
        public long Limit { get; set; }

        [JsonProperty("offset")]
        public long Offset { get; set; }

        [JsonProperty("totalNumberOfItems")]
        public long TotalNumberOfItems { get; set; }

        [JsonProperty("items")]
        public Track[] Items { get; set; }
    }

    public class Artist
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("picture")]
        public string Picture { get; set; }
    }

    public class Track
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("duration")]
        public long Duration { get; set; }

        [JsonProperty("replayGain")]
        public double ReplayGain { get; set; }

        [JsonProperty("peak")]
        public double Peak { get; set; }

        [JsonProperty("allowStreaming")]
        public bool AllowStreaming { get; set; }

        [JsonProperty("streamReady")]
        public bool StreamReady { get; set; }

        [JsonProperty("adSupportedStreamReady")]
        public bool AdSupportedStreamReady { get; set; }

        [JsonProperty("djReady")]
        public bool DjReady { get; set; }

        [JsonProperty("stemReady")]
        public bool StemReady { get; set; }

        [JsonProperty("streamStartDate")]
        public string StreamStartDate { get; set; }

        [JsonProperty("premiumStreamingOnly")]
        public bool PremiumStreamingOnly { get; set; }

        [JsonProperty("trackNumber")]
        public long TrackNumber { get; set; }

        [JsonProperty("volumeNumber")]
        public long VolumeNumber { get; set; }

        [JsonProperty("version")]
        public object Version { get; set; }

        [JsonProperty("popularity")]
        public long Popularity { get; set; }

        [JsonProperty("copyright")]
        public string Copyright { get; set; }

        [JsonProperty("url")]
        public string Url { get; set; }

        [JsonProperty("isrc")]
        public string Isrc { get; set; }

        [JsonProperty("editable")]
        public bool Editable { get; set; }

        [JsonProperty("explicit")]
        public bool Explicit { get; set; }

        [JsonProperty("audioQuality")]
        public string AudioQuality { get; set; }

        [JsonProperty("audioModes")]
        public string[] AudioModes { get; set; }

        [JsonProperty("artists")]
        public Artist[] Artists { get; set; }

        [JsonProperty("album")]
        public Album Album { get; set; }
    }
}
#pragma warning restore CS8618
