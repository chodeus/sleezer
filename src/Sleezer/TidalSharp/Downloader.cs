using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.Http;
using System.Globalization;
using TidalSharp.Data;
using TidalSharp.Downloading;
using TidalSharp.Exceptions;
using TidalSharp.Metadata;

namespace TidalSharp;

public class Downloader
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

    internal Downloader(IHttpClient client, API api, Session session)
    {
        _client = client;
        _api = api;
        _session = session;
    }

    private readonly IHttpClient _client;
    private readonly API _api;
    private readonly Session _session;

    public async Task<DownloadData<Stream>> GetRawTrackStream(string trackId, AudioQuality quality, Action<int>? onChunkDownloaded = null, CancellationToken token = default)
    {
        var (stream, manifest) = await GetTrackStream(trackId, quality, onChunkDownloaded, token);
        return new(stream, manifest.FileExtension);
    }

    public async Task<DownloadData<byte[]>> GetRawTrackBytes(string trackId, AudioQuality quality, Action<int>? onChunkDownloaded = null, CancellationToken token = default)
    {
        var (stream, manifest) = await GetTrackStream(trackId, quality, onChunkDownloaded, token);
        var data = new DownloadData<byte[]>(stream.ToArray(), manifest.FileExtension);

        await stream.DisposeAsync();

        return data;
    }

    public async Task WriteRawTrackToFile(string trackId, AudioQuality quality, string trackPath, Action<int>? onChunkDownloaded = null, CancellationToken token = default)
    {
        var (stream, manifest) = await GetTrackStream(trackId, quality, onChunkDownloaded, token);

        // Surface what Tidal actually delivered. Issue #29 reports HI_RES_LOSSLESS
        // requests returning 24-bit/48kHz instead of 24/96 — this Debug line
        // gives users a way to correlate selected quality with delivered sample
        // rate when reporting the same problem.
        _logger.Debug("Tidal track {TrackId} requested at {Requested}; manifest delivered codec={Codec} sampleRate={SampleRate}",
            trackId, quality, manifest.Codecs, manifest.SampleRate);

        // Explicit flush + async dispose so callers that immediately open the
        // file with TagLib don't read garbage box headers (issue #20).
        FileStream fileStream = File.Open(trackPath, FileMode.Create);
        try
        {
            await stream.CopyToAsync(fileStream, token);
            await fileStream.FlushAsync(token);
        }
        finally
        {
            await fileStream.DisposeAsync();
            await stream.DisposeAsync();
        }
    }

    public async Task<string> GetExtensionForTrack(string trackId, AudioQuality quality, CancellationToken token = default)
    {
        var trackStreamData = await GetTrackStreamData(trackId, quality, token);
        var streamManifest = new StreamManifest(trackStreamData);
        return streamManifest.FileExtension;
    }

    public async Task<int> GetChunksInTrack(string trackId, AudioQuality quality, CancellationToken token = default)
    {
        var trackStreamData = await GetTrackStreamData(trackId, quality, token);
        var streamManifest = new StreamManifest(trackStreamData);
        return streamManifest.Urls.Length;
    }

    public async Task<byte[]> GetImageBytes(string id, MediaResolution resolution, CancellationToken token = default)
    {
        var request = _client
            .BuildRequest(Globals.IMAGE_URL_BASE)
            .Resource(Globals.GetImageResoursePath(id, resolution));
        var response = await _client.ProcessRequestAsync(request);

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            throw new UnavailableMediaException($"The image with {id} with resolution {resolution} is unavailable.");
        }

        return response.ResponseData;
    }

    public async Task ApplyMetadataToTrackStream(string trackId, DownloadData<Stream> trackData, MediaResolution coverResolution = MediaResolution.s640, string lyrics = "", CancellationToken token = default)
    {
        byte[] magicBuffer = new byte[4];
        await trackData.Data.ReadAsync(magicBuffer.AsMemory(0, 4), token);

        trackData.Data.Seek(0, SeekOrigin.Begin);

        StreamAbstraction abstraction = new("track" + trackData.FileExtension, trackData.Data);
        using TagLib.File file = TagLib.File.Create(abstraction);
        await ApplyMetadataToTagLibFile(file, trackId, coverResolution, lyrics, token);

        trackData.Data.Seek(0, SeekOrigin.Begin);
    }

    public async Task ApplyMetadataToTrackBytes(string trackId, DownloadData<byte[]> trackData, MediaResolution coverResolution = MediaResolution.s640, string lyrics = "", CancellationToken token = default)
    {
        FileBytesAbstraction abstraction = new("track" + trackData.FileExtension, trackData.Data);
        using TagLib.File file = TagLib.File.Create(abstraction);
        await ApplyMetadataToTagLibFile(file, trackId, coverResolution, lyrics, token);

        byte[] finalData = abstraction.MemoryStream.ToArray();
        await abstraction.MemoryStream.DisposeAsync();
        trackData.Data = finalData;
    }

    public async Task ApplyMetadataToFile(string trackId, string trackPath, MediaResolution coverResolution = MediaResolution.s640, string lyrics = "", CancellationToken token = default)
    {
        using TagLib.File file = TagLib.File.Create(trackPath);
        await ApplyMetadataToTagLibFile(file, trackId, coverResolution, lyrics, token);
    }

    public async Task<(string? plainLyrics, string? syncLyrics)?> FetchLyricsFromTidal(string trackId, CancellationToken token = default)
    {
        var lyrics = await _api.GetTrackLyrics(trackId, token);
        if (lyrics == null)
            return null;

        return (lyrics.Lyrics, lyrics.Subtitles);
    }

    public async Task<(string? plainLyrics, string? syncLyrics)?> FetchLyricsFromLRCLIB(string instance, string trackName, string artistName, string albumName, int duration, CancellationToken token = default)
    {
        var requestResource = $"/api/get?artist_name={Uri.EscapeDataString(artistName)}&track_name={Uri.EscapeDataString(trackName)}&album_name={Uri.EscapeDataString(albumName)}&duration={duration}";
        var request = _client
            .BuildRequest($"https://{instance}")
            .Resource(requestResource);
        var response = await _client.ProcessRequestAsync(request);

        if (!response.HasHttpError)
        {
            var content = response.Content;
            var json = JObject.Parse(content);
            return (json["plainLyrics"]?.ToString(), json["syncedLyrics"]?.ToString());
        }

        return null;
    }

    // TODO: video downloading, this is less important as this is mainly for lidarr

    private async Task ApplyMetadataToTagLibFile(TagLib.File track, string trackId, MediaResolution coverResolution = MediaResolution.s640, string lyrics = "", CancellationToken token = default)
    {
        JToken trackData = await _api.GetTrack(trackId, token);
        string albumId = trackData["album"]!["id"]!.ToString();
        JToken albumPage = await _api.GetAlbum(albumId, token);

        byte[]? albumArt = null;
        try
        {
            albumArt = await GetImageBytes(trackData["album"]!["cover"]!.ToString(), coverResolution, token);
        }
        catch (UnavailableMediaException ex)
        {
            _logger.Debug(ex, "Album art unavailable for Tidal track {TrackId}", trackId);
        }

        track.Tag.Title = API.CompleteTitleFromPage(trackData);
        track.Tag.Album = API.CompleteTitleFromPage(albumPage);
        track.Tag.Performers = trackData["artists"]!.Select(a => a["name"]!.ToString()).ToArray();
        track.Tag.AlbumArtists = albumPage["artists"]!.Select(a => a["name"]!.ToString()).ToArray();
        string? rawReleaseDate = albumPage["releaseDate"]?.ToString() ?? albumPage["streamStartDate"]?.ToString();
        DateTime releaseDate = !string.IsNullOrEmpty(rawReleaseDate) ? DateTime.Parse(rawReleaseDate, CultureInfo.InvariantCulture) : DateTime.MinValue;
        track.Tag.Year = (uint)releaseDate.Year;
        track.Tag.Track = uint.Parse(trackData["trackNumber"]!.ToString());
        track.Tag.TrackCount = uint.Parse(albumPage["numberOfTracks"]!.ToString());
        track.Tag.Disc = uint.Parse(trackData["volumeNumber"]!.ToString());
        track.Tag.DiscCount = uint.Parse(albumPage["numberOfVolumes"]!.ToString());
        if (albumArt != null)
            track.Tag.Pictures = [new TagLib.Picture(new TagLib.ByteVector(albumArt))];
        track.Tag.Lyrics = lyrics;

        track.Save();
    }

    // TODO: implement method to extract flacs from the m4a containers
    // tidal-dl-ng uses ffmpeg but thats not ideal in this case
    private async Task<(MemoryStream stream, StreamManifest manifest)> GetTrackStream(string trackId, AudioQuality quality, Action<int>? onChunkDownloaded = null, CancellationToken token = default)
    {
        var trackStreamData = await GetTrackStreamData(trackId, quality, token);
        var streamManifest = new StreamManifest(trackStreamData);

        var urls = streamManifest.Urls;

        var outStream = new MemoryStream();

        for (int i = 0; i < urls.Length; i++)
        {
            var url = urls[i];

            var request = _client
                .BuildRequest(url);
            var response = await _client.ProcessRequestAsync(request);

            outStream.Write(response.ResponseData);
            onChunkDownloaded?.Invoke(i+1);
        }

        // TODO: test decryption, don't know of any tracks yet that need it

        if (!string.IsNullOrEmpty(streamManifest.EncryptionKey))
        {
            var (key, nonce) = Decryption.DecryptSecurityToken(streamManifest.EncryptionKey);
            var decryptedStream = new MemoryStream();
            Decryption.DecryptStream(outStream, decryptedStream, key, nonce);

            decryptedStream.Seek(0, SeekOrigin.Begin);
            await outStream.DisposeAsync();
            return (decryptedStream, streamManifest);
        }

        outStream.Seek(0, SeekOrigin.Begin);
        return (outStream, streamManifest);
    }

    private async Task<TrackStreamData> GetTrackStreamData(string trackId, AudioQuality quality, CancellationToken token = default)
    {
        if (_cachedStreamData.TryGetValue((trackId, quality), out TrackStreamData? data))
            return data;

        var result = await _api.Call(HttpMethod.Get, $"tracks/{trackId}/playbackinfopostpaywall",
            urlParameters: new()
            {
                { "playbackmode", "STREAM" },
                { "assetpresentation", "FULL" },
                { "audioquality", $"{quality}" }
            },
            token: token
        );
        var streamData = result.ToObject<TrackStreamData>()!;
        lock (_cachedStreamData)
            _cachedStreamData.Add((trackId, quality), streamData);
        return streamData;
    }

    private Dictionary<(string trackId, AudioQuality quality), TrackStreamData> _cachedStreamData = [];
}

public class DownloadData<T>(T data, string fileExtension) : IDisposable
{
    public T Data { get; set; } = data;
    public string FileExtension { get; set; } = fileExtension;

    public void Dispose()
    {
        if (Data is Stream stream)
            stream.Dispose();

        GC.SuppressFinalize(this);
    }
}
