using Newtonsoft.Json;

namespace TidalSharp.Data;

public partial class TrackStreamData
{
    [JsonProperty("trackId")]
    public long TrackId { get; set; }

    [JsonProperty("assetPresentation")]
    public string? AssetPresentation { get; set; }

    [JsonProperty("audioMode")]
    public string? AudioMode { get; set; }

    [JsonProperty("audioQuality")]
    public string? AudioQuality { get; set; }

    [JsonProperty("manifestMimeType")]
    public string? ManifestMimeType { get; set; }

    [JsonProperty("manifestHash")]
    public string? ManifestHash { get; set; }

    [JsonProperty("manifest")]
    public string? Manifest { get; set; }

    [JsonProperty("albumReplayGain")]
    public double AlbumReplayGain { get; set; }

    [JsonProperty("albumPeakAmplitude")]
    public double AlbumPeakAmplitude { get; set; }

    [JsonProperty("trackReplayGain")]
    public double TrackReplayGain { get; set; }

    [JsonProperty("trackPeakAmplitude")]
    public double TrackPeakAmplitude { get; set; }

    [JsonProperty("bitDepth")]
    public long BitDepth { get; set; }

    [JsonProperty("sampleRate")]
    public long SampleRate { get; set; }
}
