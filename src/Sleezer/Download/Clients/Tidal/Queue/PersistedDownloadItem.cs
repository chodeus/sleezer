using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using TidalSharp.Data;

namespace NzbDrone.Core.Download.Clients.Tidal.Queue
{
    // On-disk record written as a sidecar next to the completed audio files so
    // Lidarr can re-discover Tidal downloads after the plugin restarts. Only
    // the fields ToDownloadClientItem actually reads are persisted — RemoteAlbum
    // and the live TidalSharp handles don't round-trip and aren't needed once a
    // download has completed and post-processing has finished.
    //
    // Mirrors the Deezer persistence pattern (Deezer/Queue/PersistedDownloadItem.cs).
    public class PersistedDownloadItem
    {
        public const string SidecarFileName = ".sleezer-tidal-state.json";

        // Bumped when the schema changes incompatibly so old sidecars can be ignored.
        public const int CurrentSchemaVersion = 1;

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = false,
            Converters = { new JsonStringEnumConverter() },
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        public string ID { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public bool Explicit { get; set; }

        public AudioQuality Bitrate { get; set; }
        public long TotalSize { get; set; }
        public string DownloadFolder { get; set; } = string.Empty;
        public DownloadItemStatus Status { get; set; }

        public static string SidecarPath(string downloadFolder)
            => Path.Combine(downloadFolder, SidecarFileName);

        public static PersistedDownloadItem CaptureFrom(DownloadItem item)
        {
            return new PersistedDownloadItem
            {
                ID = item.ID,
                Title = item.Title,
                Artist = item.Artist,
                Explicit = item.Explicit,
                Bitrate = item.Bitrate,
                TotalSize = item.TotalSize,
                DownloadFolder = item.DownloadFolder ?? string.Empty,
                Status = item.Status,
            };
        }

        public void WriteTo(string folder)
        {
            string path = SidecarPath(folder);
            string json = JsonSerializer.Serialize(this, SerializerOptions);
            File.WriteAllText(path, json);
        }

        public static PersistedDownloadItem? TryRead(string sidecarPath)
        {
            string json = File.ReadAllText(sidecarPath);
            PersistedDownloadItem? parsed = JsonSerializer.Deserialize<PersistedDownloadItem>(json, SerializerOptions);
            if (parsed == null || parsed.SchemaVersion != CurrentSchemaVersion)
                return null;
            return parsed;
        }
    }
}
