using Newtonsoft.Json;

namespace TidalSharp.Data;

public partial class TidalLyrics
{
    [JsonProperty("trackId")]
    public long TrackId { get; set; }

    [JsonProperty("lyricsProvider")]
    public string? LyricsProvider { get; set; }

    [JsonProperty("lyrics")]
    public string? Lyrics { get; set; }

    [JsonProperty("subtitles")]
    public string? Subtitles { get; set; }

    [JsonProperty("isRightToLeft")]
    public bool IsRightToLeft { get; set; }
}
