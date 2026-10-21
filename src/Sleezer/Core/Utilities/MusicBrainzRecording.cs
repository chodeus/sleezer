using System.Text.Json;

namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    /// <summary>A recording's title and length as MusicBrainz files it; Lidarr holds only the recordings on its releases.</summary>
    public sealed record MusicBrainzRecording(string Title, int LengthMs)
    {
        /// <summary>From a /ws/2/recording lookup; a recording with no length gives 0.</summary>
        public static MusicBrainzRecording FromJson(string json)
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            int length = root.TryGetProperty("length", out JsonElement ms) && ms.ValueKind == JsonValueKind.Number ? ms.GetInt32() : 0;
            return new MusicBrainzRecording(root.GetProperty("title").GetString() ?? string.Empty, length);
        }
    }
}
