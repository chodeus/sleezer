using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using NzbDrone.Plugin.Sleezer.Qobuz;

namespace NzbDrone.Core.Download.Clients.Qobuz.Queue
{
    // On-disk record written beside the completed audio so Lidarr can re-discover
    // Qobuz downloads after the plugin restarts. Only the fields
    // ToDownloadClientItem reads are persisted — RemoteAlbum and the live
    // QobuzApiSharp handles neither round-trip nor matter once a download has
    // completed and post-processing has finished.
    //
    // Mirrors the Deezer and Tidal persistence pattern.
    public class PersistedDownloadItem
    {
        public const string SidecarFileName = ".sleezer-qobuz-state.json";

        // Bumped when the schema changes incompatibly so old sidecars are ignored.
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
        public int TrackCount { get; set; }
        public string DownloadFolder { get; set; } = string.Empty;
        public DownloadItemStatus Status { get; set; }

        public static string SidecarPath(string downloadFolder)
            => Path.Combine(downloadFolder, SidecarFileName);

        public static PersistedDownloadItem CaptureFrom(DownloadItem item) => new()
        {
            ID = item.ID,
            Title = item.Title,
            Artist = item.Artist,
            Explicit = item.Explicit,
            Bitrate = item.Bitrate,
            TotalSize = item.TotalSize,
            TrackCount = item.TrackCount,
            DownloadFolder = item.DownloadFolder ?? string.Empty,
            Status = item.Status,
        };

        public void WriteTo(string folder)
        {
            string json = JsonSerializer.Serialize(this, SerializerOptions);
            File.WriteAllText(SidecarPath(folder), json);
        }

        public static PersistedDownloadItem? TryRead(string sidecarPath)
        {
            string json = File.ReadAllText(sidecarPath);
            PersistedDownloadItem? parsed = JsonSerializer.Deserialize<PersistedDownloadItem>(json, SerializerOptions);

            return parsed?.SchemaVersion == CurrentSchemaVersion ? parsed : null;
        }
    }
}
