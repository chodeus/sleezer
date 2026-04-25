using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Tidal;
using TagLib;
using TidalSharp;
using TidalSharp.Data;

namespace NzbDrone.Core.Download.Clients.Tidal.Queue
{
    public class DownloadItem
    {
        // Time to wait between TagLib retries when the underlying file isn't
        // visible yet to the metadata read (NFS, Unraid mover, etc.).
        private static readonly TimeSpan TagLibRetryDelay = TimeSpan.FromMilliseconds(500);

        public static async Task<DownloadItem?> From(RemoteAlbum remoteAlbum)
        {
            var url = remoteAlbum.Release.DownloadUrl.Trim();
            var quality = remoteAlbum.Release.Container switch
            {
                "96" => AudioQuality.LOW,
                "320" => AudioQuality.HIGH,
                "Lossless" => AudioQuality.LOSSLESS,
                "24bit Lossless" => AudioQuality.HI_RES_LOSSLESS,
                _ => AudioQuality.HIGH,
            };

            if (!url.Contains("tidal", StringComparison.CurrentCultureIgnoreCase))
                return null;

            if (!TidalURL.TryParse(url, out var tidalUrl))
                return null;

            DownloadItem item = new()
            {
                ID = Guid.NewGuid().ToString(),
                Status = DownloadItemStatus.Queued,
                Bitrate = quality,
                RemoteAlbum = remoteAlbum,
                _tidalUrl = tidalUrl,
            };

            await item.SetTidalData();
            return item;
        }

        public string ID { get; private set; } = "";
        public string Title { get; private set; } = "";
        public string Artist { get; private set; } = "";
        public bool Explicit { get; private set; }

        public RemoteAlbum? RemoteAlbum { get; private set; }
        public string? DownloadFolder { get; private set; }

        public AudioQuality Bitrate { get; private set; }
        public DownloadItemStatus Status { get; set; }

        public float Progress => DownloadedSize / (float)Math.Max(TotalSize, 1);
        public long DownloadedSize { get; private set; }
        public long TotalSize { get; private set; }

        public int FailedTracks => _failedTracks;
        public int TotalTracks => _tracks?.Length ?? 0;

        private (string id, int chunks)[]? _tracks;
        private TidalURL? _tidalUrl;
        private JObject? _tidalAlbum;

        public async Task DoDownload(TidalSettings settings, Logger logger, CancellationToken cancellation = default)
        {
            List<Task> tasks = new();
            using SemaphoreSlim semaphore = new(3, 3);

            foreach (var (trackId, _) in _tracks ?? Array.Empty<(string, int)>())
            {
                tasks.Add(Task.Run(async () =>
                {
                    await semaphore.WaitAsync(cancellation);
                    try
                    {
                        await DoTrackDownload(trackId, settings, logger, cancellation);
                        if (settings.DownloadDelay)
                        {
                            float delay = (float)Random.Shared.NextDouble()
                                * (settings.DownloadDelayMax - settings.DownloadDelayMin)
                                + settings.DownloadDelayMin;
                            await Task.Delay((int)(delay * 1000), cancellation);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // bubble cancellation up via task aggregation
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, "Error while downloading Tidal track {TrackId}", trackId);
                        Interlocked.Increment(ref _failedTracks);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, cancellation));
            }

            await Task.WhenAll(tasks);

            // Issue #30 — partial album: if any track failed, mark the whole
            // download Failed so Lidarr re-queues against another release
            // instead of importing the partial set as if it were complete.
            if (FailedTracks > 0)
            {
                logger.Warn("Tidal album {Title}: {Failed}/{Total} tracks failed; marking item Failed", Title, FailedTracks, TotalTracks);
                Status = DownloadItemStatus.Failed;
            }
            else
            {
                Status = DownloadItemStatus.Completed;
            }
        }

        private int _failedTracks;

        private async Task DoTrackDownload(string track, TidalSettings settings, Logger logger, CancellationToken cancellation = default)
        {
            var instance = TidalAPI.Instance
                ?? throw new InvalidOperationException("Tidal API not initialized");
            var page = await instance.Client.API.GetTrack(track, cancellation);
            string songTitle = API.CompleteTitleFromPage(page);
            string artistName = page["artist"]!["name"]!.ToString();
            string albumTitle = page["album"]!["title"]!.ToString();
            int duration = page["duration"]!.Value<int>();

            string ext = (await instance.Client.Downloader.GetExtensionForTrack(track, Bitrate, cancellation)).TrimStart('.');
            string outPath = Path.Combine(
                settings.DownloadPath,
                MetadataUtilities.GetFilledTemplate("%albumartist%/%album%/", ext, page, _tidalAlbum!),
                MetadataUtilities.GetFilledTemplate("%volume% - %track% - %title%.%ext%", ext, page, _tidalAlbum!));
            string outDir = Path.GetDirectoryName(outPath)!;

            DownloadFolder = outDir;
            if (!Directory.Exists(outDir))
                Directory.CreateDirectory(outDir);

            await instance.Client.Downloader.WriteRawTrackToFile(track, Bitrate, outPath, _ => DownloadedSize++, cancellation);
            outPath = HandleAudioConversion(outPath, settings, logger);

            string plainLyrics = string.Empty;
            string? syncLyrics = null;

            var lyrics = await instance.Client.Downloader.FetchLyricsFromTidal(track, cancellation);
            if (lyrics.HasValue)
            {
                plainLyrics = lyrics.Value.plainLyrics ?? string.Empty;
                if (settings.SaveSyncedLyrics)
                    syncLyrics = lyrics.Value.syncLyrics;
            }

            if (settings.UseLRCLIB && (string.IsNullOrWhiteSpace(plainLyrics) || (settings.SaveSyncedLyrics && !(syncLyrics?.Any() ?? false))))
            {
                lyrics = await instance.Client.Downloader.FetchLyricsFromLRCLIB("lrclib.net", songTitle, artistName, albumTitle, duration, cancellation);
                if (lyrics.HasValue)
                {
                    if (string.IsNullOrWhiteSpace(plainLyrics))
                        plainLyrics = lyrics.Value.plainLyrics ?? string.Empty;
                    if (settings.SaveSyncedLyrics && !(syncLyrics?.Any() ?? false))
                        syncLyrics = lyrics.Value.syncLyrics;
                }
            }

            await ApplyMetadataWithRetry(track, outPath, plainLyrics, logger, cancellation);

            if (syncLyrics != null)
            {
                string lrcPath = Path.Combine(outDir,
                    MetadataUtilities.GetFilledTemplate("%volume% - %track% - %title%.%ext%", "lrc", page, _tidalAlbum!));
                await System.IO.File.WriteAllTextAsync(lrcPath, syncLyrics, cancellation);
            }
        }

        // Issue #20 — TagLib.CorruptFileException on M4A box header. Even
        // with explicit flush+dispose in WriteRawTrackToFile, NFS/Unraid
        // mover targets can return a stale view for a few hundred ms.
        // Retry once with a short delay before treating it as corrupt.
        private static async Task ApplyMetadataWithRetry(string track, string outPath, string lyrics, Logger logger, CancellationToken cancellation)
        {
            var instance = TidalAPI.Instance!;
            try
            {
                await instance.Client.Downloader.ApplyMetadataToFile(track, outPath, MediaResolution.s640, lyrics, token: cancellation);
            }
            catch (CorruptFileException ex)
            {
                logger.Debug(ex, "TagLib reported corrupt M4A for {Path}; waiting {DelayMs}ms then retrying", outPath, TagLibRetryDelay.TotalMilliseconds);
                await Task.Delay(TagLibRetryDelay, cancellation);
                await instance.Client.Downloader.ApplyMetadataToFile(track, outPath, MediaResolution.s640, lyrics, token: cancellation);
            }
        }

        private string HandleAudioConversion(string filePath, TidalSettings settings, Logger logger)
        {
            if (!settings.ExtractFlac && !settings.ReEncodeAAC)
                return filePath;

            string[] codecs;
            try
            {
                codecs = FFMPEG.ProbeCodecs(filePath);
            }
            catch (FFMPEGException ex)
            {
                // ffprobe not installed or otherwise broken. Skip the
                // conversion step so the track download as a whole still
                // succeeds — Lidarr will import the original M4A. Logged
                // Warn (not Error) since the user may have intentionally
                // toggled the conversion options on a host without ffmpeg.
                logger.Warn(ex, "Tidal: skipping audio conversion for {Path} — ffprobe unavailable", filePath);
                return filePath;
            }
            if (codecs.Contains("flac") && settings.ExtractFlac)
            {
                string newFilePath = Path.ChangeExtension(filePath, "flac");
                try
                {
                    FFMPEG.ConvertWithoutReencode(filePath, newFilePath);
                    if (System.IO.File.Exists(filePath))
                        System.IO.File.Delete(filePath);
                    return newFilePath;
                }
                catch (FFMPEGException ex)
                {
                    logger.Warn(ex, "FLAC extract failed for {Path}; leaving original M4A in place", filePath);
                    if (System.IO.File.Exists(newFilePath))
                        System.IO.File.Delete(newFilePath);
                    return filePath;
                }
            }

            if (codecs.Contains("aac") && settings.ReEncodeAAC)
            {
                string newFilePath = Path.ChangeExtension(filePath, "mp3");
                try
                {
                    using var tagFile = TagLib.File.Create(filePath);
                    int bitrate = tagFile.Properties.AudioBitrate;

                    FFMPEG.Reencode(filePath, newFilePath, bitrate);
                    if (System.IO.File.Exists(filePath))
                        System.IO.File.Delete(filePath);
                    return newFilePath;
                }
                catch (FFMPEGException ex)
                {
                    logger.Warn(ex, "AAC re-encode failed for {Path}; leaving original M4A in place", filePath);
                    if (System.IO.File.Exists(newFilePath))
                        System.IO.File.Delete(newFilePath);
                    return filePath;
                }
            }

            return filePath;
        }

        private async Task SetTidalData(CancellationToken cancellation = default)
        {
            if (_tidalUrl == null || _tidalUrl.EntityType != EntityType.Album)
                throw new InvalidOperationException();

            var instance = TidalAPI.Instance
                ?? throw new InvalidOperationException("Tidal API not initialized");

            var album = await instance.Client.API.GetAlbum(_tidalUrl.Id, cancellation);
            var albumTracks = await instance.Client.API.GetAlbumTracks(_tidalUrl.Id, cancellation);

            var tracksTasks = albumTracks["items"]!.Select(async t =>
            {
                int chunks = await instance.Client.Downloader.GetChunksInTrack(t["id"]!.ToString(), Bitrate, cancellation);
                return (t["id"]!.ToString(), chunks);
            }).ToArray();

            var tracks = await Task.WhenAll(tracksTasks);
            _tracks = tracks;

            _tidalAlbum = album;

            Title = album["title"]!.ToString();
            Artist = album["artist"]!["name"]!.ToString();
            Explicit = album["explicit"]!.Value<bool>();
            TotalSize = _tracks.Sum(t => t.chunks);
        }
    }
}
