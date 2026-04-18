using System.Text.Json.Serialization;

namespace NzbDrone.Plugin.Sleezer.Indexers.TripleTriple
{
    public record TripleTripleStatusResponse(
        [property: JsonPropertyName("amazonMusic")] string AmazonMusic);

    public record TripleTripleSearchResponse(
        [property: JsonPropertyName("results")] List<TripleTripleResult>? Results);

    public record TripleTripleResult(
        [property: JsonPropertyName("hits")] List<TripleTripleSearchHit>? Hits);

    public record TripleTripleSearchHit(
        [property: JsonPropertyName("document")] TripleTripleDocument? Document);

    public record TripleTripleDocument(
        [property: JsonPropertyName("__type")] string Type,
        [property: JsonPropertyName("asin")] string Asin,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("artistName")] string ArtistName,
        [property: JsonPropertyName("duration")] int Duration,
        [property: JsonPropertyName("trackNum")] int TrackNum,
        [property: JsonPropertyName("discNum")] int DiscNum,
        [property: JsonPropertyName("albumName")] string? AlbumName = null,
        [property: JsonPropertyName("albumAsin")] string? AlbumAsin = null,
        [property: JsonPropertyName("artistAsin")] string? ArtistAsin = null,
        [property: JsonPropertyName("artOriginal")] TripleTripleArt? ArtOriginal = null,
        [property: JsonPropertyName("originalReleaseDate")] long? OriginalReleaseDate = null,
        [property: JsonPropertyName("primaryGenre")] string? PrimaryGenre = null,
        [property: JsonPropertyName("isrc")] string? Isrc = null,
        [property: JsonPropertyName("isMusicSubscription")] bool IsMusicSubscription = false)
    {
        public bool IsTrack => Type?.Contains("Track") == true;
        public bool IsAlbum => Type?.Contains("Album") == true;
    }

    public record TripleTripleArt(
        [property: JsonPropertyName("URL")] string Url,
        [property: JsonPropertyName("artUrl")] string? ArtUrl);

    public record TripleTripleMediaResponse(
        [property: JsonPropertyName("asin")] string Asin,
        [property: JsonPropertyName("stremeable")] bool? StremeableTypo,  // API typo version
        [property: JsonPropertyName("streamable")] bool? StreamableCorrect,  // Correct spelling
        [property: JsonPropertyName("tags")] TripleTripleTags? Tags,
        [property: JsonPropertyName("templateCoverUrl")] string? TemplateCoverUrl,
        [property: JsonPropertyName("streamInfo")] TripleTripleStreamInfo? StreamInfo,
        [property: JsonPropertyName("lyrics")] TripleTripleLyrics? Lyrics,
        [property: JsonPropertyName("decryptionKey")] string? DecryptionKey)
    {
        [JsonIgnore]
        public bool Streamable => StreamableCorrect ?? StremeableTypo ?? false;
    };

    public record TripleTripleTags(
        [property: JsonPropertyName("album")] string? Album,
        [property: JsonPropertyName("albumArtist")] string? AlbumArtist,
        [property: JsonPropertyName("artist")] string? Artist,
        [property: JsonPropertyName("composer")] string? Composer,
        [property: JsonPropertyName("copyright")] string? Copyright,
        [property: JsonPropertyName("date")] string? Date,
        [property: JsonPropertyName("genre")] string? Genre,
        [property: JsonPropertyName("isrc")] string? Isrc,
        [property: JsonPropertyName("label")] string? Label,
        [property: JsonPropertyName("lyrics")] string? PlainLyrics,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("disc")] int Disc = 1,
        [property: JsonPropertyName("discTotal")] int DiscTotal = 1,
        [property: JsonPropertyName("track")] int Track = 0,
        [property: JsonPropertyName("trackTotal")] int TrackTotal = 0);

    public record TripleTripleStreamInfo(
        [property: JsonPropertyName("format")] string Format,
        [property: JsonPropertyName("streamUrl")] string StreamUrl,
        [property: JsonPropertyName("pssh")] string? Pssh,
        [property: JsonPropertyName("kid")] string? Kid,
        [property: JsonPropertyName("codec")] string Codec,
        [property: JsonPropertyName("sampleRate")] int SampleRate);

    public record TripleTripleLyrics(
        [property: JsonPropertyName("synced")] string? Synced,
        [property: JsonPropertyName("unsynced")] string? Unsynced);

    public record TripleTripleAlbumMetadata(
        [property: JsonPropertyName("albumList")] List<TripleTripleAlbumInfo>? AlbumList);

    public record TripleTripleAlbumInfo(
        [property: JsonPropertyName("asin")] string Asin,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("artist")] TripleTripleArtistInfo Artist,
        [property: JsonPropertyName("image")] string? Image,
        [property: JsonPropertyName("label")] string? Label,
        [property: JsonPropertyName("originalReleaseDate")] long OriginalReleaseDate,
        [property: JsonPropertyName("merchantReleaseDate")] long? MerchantReleaseDate,
        [property: JsonPropertyName("duration")] int Duration,
        [property: JsonPropertyName("trackCount")] int TrackCount,
        [property: JsonPropertyName("isMusicSubscription")] bool IsMusicSubscription,
        [property: JsonPropertyName("primaryGenreName")] string? PrimaryGenreName,
        [property: JsonPropertyName("tracks")] List<TripleTripleTrackInfo>? Tracks);

    public record TripleTripleArtistInfo(
        [property: JsonPropertyName("asin")] string Asin,
        [property: JsonPropertyName("name")] string Name);

    public record TripleTripleTrackInfo(
        [property: JsonPropertyName("asin")] string Asin,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("trackNum")] int TrackNum,
        [property: JsonPropertyName("duration")] int Duration,
        [property: JsonPropertyName("isrc")] string? Isrc,
        [property: JsonPropertyName("songWriters")] List<string>? SongWriters,
        [property: JsonPropertyName("lyrics")] TripleTripleLyrics? Lyrics,
        [property: JsonPropertyName("streamInfo")] TripleTripleStreamInfo? StreamInfo);

    public record TripleTripleRequestData(
        [property: JsonPropertyName("baseUrl")] string BaseUrl,
        [property: JsonPropertyName("country")] string Country,
        [property: JsonPropertyName("codec")] string Codec,
        [property: JsonPropertyName("isSingle")] bool IsSingle);
}
