using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.Http;
using System.Globalization;
using TidalSharp.Data;
using TidalSharp.Downloading;
using TidalSharp.Exceptions;
using NzbDrone.Plugin.Sleezer.Core.Utilities;

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

    public async Task WriteRawTrackToFile(string trackId, AudioQuality quality, string trackPath, Action<int>? onChunkDownloaded = null, CancellationToken token = default)
    {
        var trackStreamData = await GetTrackStreamData(trackId, quality, token);
        var manifest = new StreamManifest(trackStreamData);

        // Encrypted delivery is unsupported — refuse on the manifest's type, not just a present key.
        if (!string.Equals(manifest.EncryptionType, "NONE", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(manifest.EncryptionKey))
            throw new UnavailableMediaException($"Tidal returned an encrypted stream ({manifest.EncryptionType}) for track {trackId}; encrypted delivery is not supported.");

        // Surface what Tidal actually delivered. Issue #29 reports HI_RES_LOSSLESS
        // requests returning 24-bit/48kHz instead of 24/96 — this Debug line
        // gives users a way to correlate selected quality with delivered sample
        // rate when reporting the same problem.
        _logger.Debug("Tidal track {TrackId} requested at {Requested}; manifest delivered codec={Codec} sampleRate={SampleRate}",
            trackId, quality, manifest.Codecs, manifest.SampleRate);

        // Segments stream into a .part file that moves into place only when whole, so a failure never leaves a track to import.
        string partPath = trackPath + ".part";
        try
        {
            await using (var file = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                for (int i = 0; i < manifest.Urls.Length; i++)
                {
                    var response = await TransientHttp.SendAsync(_client, _client.BuildRequest(manifest.Urls[i]), token);
                    if (response.HasHttpError)
                        throw new APIException($"Tidal's CDN answered segment {i + 1} of {manifest.Urls.Length} for track {trackId} with HTTP {(int)response.StatusCode}.");

                    await file.WriteAsync(response.ResponseData, token);
                    onChunkDownloaded?.Invoke(i + 1);
                }

                await file.FlushAsync(token);
            }

            File.Move(partPath, trackPath, overwrite: true);
        }
        catch
        {
            TryDelete(partPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Could not remove partial Tidal download {Path}", path);
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

    public async Task ApplyMetadataToFile(string trackId, string trackPath, MediaResolution coverResolution = MediaResolution.s640, string lyrics = "", string[]? composers = null, CancellationToken token = default)
    {
        using TagLib.File file = TagLib.File.Create(trackPath);
        await ApplyMetadataToTagLibFile(file, trackId, coverResolution, lyrics, composers, token);
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

    private async Task ApplyMetadataToTagLibFile(TagLib.File track, string trackId, MediaResolution coverResolution, string lyrics, string[]? composers, CancellationToken token)
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

        track.Tag.Title = StoreVersionFilter.TitleWithVersion(trackData["title"]!.ToString(), trackData["version"]?.ToString());
        track.Tag.Album = StoreVersionFilter.TitleWithVersion(albumPage["title"]!.ToString(), albumPage["version"]?.ToString());
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
        track.Tag.ISRC = trackData["isrc"]?.ToString();
        track.Tag.Copyright = trackData["copyright"]?.ToString() ?? albumPage["copyright"]?.ToString();
        if (trackData["bpm"]?.Type == JTokenType.Integer && trackData["bpm"]!.Value<int>() > 0)
            track.Tag.BeatsPerMinute = trackData["bpm"]!.Value<uint>();
        if (composers is { Length: > 0 })
            track.Tag.Composers = composers;

        track.Save();
    }

    // Tidal returns this userMessage from playbackinfopostpaywall when a track
    // exists in metadata but the requested audioquality isn't streamable for
    // this user/region (e.g. region-locked, removed, or only available at
    // lower qualities). We treat it as an "unavailable at this quality"
    // signal and fall back within the same lossy/lossless tier.
    private const string AssetNotReadyMessage = "Asset is not ready for playback";

    // Tier-locked fallback chains. Crossing the lossless/lossy boundary would
    // violate the user's Lidarr quality profile (a Lossless profile would
    // never accept a 320kbps AAC substitute), so the chain only contains
    // qualities of the same tier as the requested one. Sleezer-specific
    // logic — preserve on any future TidalSharp upstream sync.
    private static AudioQuality[] GetTierFallbackChain(AudioQuality requested) => requested switch
    {
        AudioQuality.HI_RES_LOSSLESS => [AudioQuality.HI_RES_LOSSLESS, AudioQuality.LOSSLESS],
        AudioQuality.LOSSLESS => [AudioQuality.LOSSLESS],
        AudioQuality.HIGH => [AudioQuality.HIGH],
        AudioQuality.LOW => [AudioQuality.LOW],
        _ => [requested]
    };

    private async Task<TrackStreamData> GetTrackStreamData(string trackId, AudioQuality quality, CancellationToken token = default)
    {
        if (_cachedStreamData.TryGetValue((trackId, quality), out TrackStreamData? data))
            return data;

        var chain = GetTierFallbackChain(quality);
        var attempted = new List<AudioQuality>(chain.Length);
        APIException? lastUnavailable = null;

        foreach (var attemptQuality in chain)
        {
            attempted.Add(attemptQuality);

            // Re-check cache for the fallback quality — if a previous track in
            // the same release already established the delivered tier, we can
            // skip the round-trip.
            if (_cachedStreamData.TryGetValue((trackId, attemptQuality), out TrackStreamData? cached))
                return cached;

            try
            {
                var result = await _api.Call(HttpMethod.Get, $"tracks/{trackId}/playbackinfopostpaywall",
                    urlParameters: new()
                    {
                        { "playbackmode", "STREAM" },
                        { "assetpresentation", "FULL" },
                        { "audioquality", $"{attemptQuality}" }
                    },
                    token: token
                );
                var streamData = result.ToObject<TrackStreamData>()!;

                // Fails the whole album like the codec check below, so Lidarr picks another source.
                if (PreviewGuard.IsPreview(streamData.AssetPresentation))
                {
                    throw new APIException(
                        $"Tidal served a {streamData.AssetPresentation} of track {trackId} instead of the full track " +
                        "— the account's subscription does not cover it. Failing the download so Lidarr can try another source.");
                }

                if (StereoGuard.IsNotStereo(streamData.AudioMode))
                {
                    throw new APIException(
                        $"Tidal served {streamData.AudioMode} audio for track {trackId} instead of stereo, which Sleezer doesn't download. " +
                        "Failing the download so Lidarr can try another source.");
                }

                // Tidal can answer a lossless request with AAC and no error: a track not licensed lossless, or every
                // track when it caps the token's client (#173). Failing aborts the whole album so Lidarr re-picks.
                if (LosslessGuard.IsLosslessTier(attemptQuality))
                {
                    string? deliveredCodec = TryReadManifestCodec(streamData);
                    if (deliveredCodec != null && !LosslessGuard.CodecIsLossless(deliveredCodec))
                    {
                        throw new APIException(
                            $"Tidal returned codec '{deliveredCodec}' for track {trackId} despite a {attemptQuality} request. " +
                            $"It may not be licensed lossless in {_api.CountryCode}; if every Tidal download fails this way, " +
                            "Tidal is likely capping what Sleezer's login may play. Failing the download so Lidarr can try another source.");
                    }
                }

                lock (_cachedStreamData)
                    _cachedStreamData[(trackId, attemptQuality)] = streamData;

                if (attemptQuality != quality)
                    _logger.Info("Tidal track {TrackId} requested at {Requested} but delivered at {Delivered} (track unavailable at requested quality)",
                        trackId, quality, attemptQuality);

                return streamData;
            }
            catch (APIException ex) when (ex.Message.Contains(AssetNotReadyMessage, StringComparison.OrdinalIgnoreCase))
            {
                lastUnavailable = ex;
                if (chain.Length > 1 && attemptQuality != chain[^1])
                {
                    var next = chain[Array.IndexOf(chain, attemptQuality) + 1];
                    _logger.Warn("Tidal track {TrackId} not available at {Requested}; trying {Fallback} (same tier)",
                        trackId, attemptQuality, next);
                }
            }
        }

        throw new APIException(
            $"Tidal couldn't deliver track {trackId} in any quality of the same tier (tried: {string.Join(", ", attempted)}). " +
            $"It may not be licensed in {_api.CountryCode}, or may have been removed; if every Tidal download fails this way, Tidal is likely refusing playback to Sleezer's login.",
            lastUnavailable!);
    }

    private Dictionary<(string trackId, AudioQuality quality), TrackStreamData> _cachedStreamData = [];

    // Best-effort codec read from a manifest. Returns null if the manifest
    // can't be parsed — caller treats that as "don't trigger the silent-
    // downgrade rejection" so a parse failure surfaces through the normal
    // WriteRawTrackToFile path with a more specific exception.
    private static string? TryReadManifestCodec(TrackStreamData data)
    {
        try
        {
            return new Downloading.StreamManifest(data).Codecs;
        }
        catch
        {
            return null;
        }
    }
}
