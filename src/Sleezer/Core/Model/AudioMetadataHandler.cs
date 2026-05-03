using NLog;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using NzbDrone.Plugin.Sleezer.Core.PostProcessing;
using NzbDrone.Plugin.Sleezer.Core.Records;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Downloader;
// Aliased so `XabeFFmpeg` can't be shadowed by our local Metadata.FFmpeg namespace.
using XabeFFmpeg = Xabe.FFmpeg.FFmpeg;

namespace NzbDrone.Plugin.Sleezer.Core.Model
{
    internal class AudioMetadataHandler
    {
        private readonly Logger? _logger;
        private static bool? _isFFmpegInstalled = null;

        public string TrackPath { get; private set; }
        public Lyric? Lyric { get; set; }

        /// <summary>
        /// Cover art bytes preserved across codec conversions in <see cref="TryConvertToFormatAsync"/>.
        /// No longer used for tagging — Lidarr's <c>IAudioTagService</c> handles cover embedding
        /// via its own media-cover cache.
        /// </summary>
        public byte[]? AlbumCover { get; set; }

        public AudioMetadataHandler(string originalPath)
        {
            TrackPath = originalPath;
            _logger = NzbDroneLogger.GetLogger(this);
        }

        /// <summary>
        /// Base codec parameters that don't change with bitrate settings
        /// </summary>
        private static readonly Dictionary<AudioFormat, string[]> BaseConversionParameters = new()
        {
            { AudioFormat.AAC,    new[] { "-codec:a aac", "-movflags +faststart", "-aac_coder twoloop" } },
            { AudioFormat.MP3,    new[] { "-codec:a libmp3lame" } },
            { AudioFormat.Opus,   new[] { "-codec:a libopus", "-vbr on", "-application audio", "-vn" } },
            { AudioFormat.Vorbis, new[] { "-codec:a libvorbis" } },
            { AudioFormat.FLAC,   new[] { "-codec:a flac", "-compression_level 8" } },
            { AudioFormat.ALAC,   new[] { "-codec:a alac" } },
            { AudioFormat.WAV,    new[] { "-codec:a pcm_s16le", "-ar 44100" } },
            { AudioFormat.MP4,    new[] { "-codec:a aac", "-movflags +faststart", "-aac_coder twoloop" } },
            { AudioFormat.AIFF,   new[] { "-codec:a pcm_s16be" } },
            { AudioFormat.OGG,    new[] { "-codec:a libvorbis" } },
            { AudioFormat.AMR,    new[] { "-codec:a libopencore_amrnb", "-ar 8000" } },
            { AudioFormat.WMA,    new[] { "-codec:a wmav2" } }
        };

        /// <summary>
        /// Format-specific bitrate/quality parameter templates
        /// </summary>
        private static readonly Dictionary<AudioFormat, Func<int, string[]>> QualityParameters = new()
        {
            {
                AudioFormat.AAC,
                bitrate => bitrate < 256
                    ? [$"-b:a {bitrate}k"]
                    : ["-q:a 2"] // 2 is highest quality for AAC
            },

            {
                AudioFormat.MP3,
                bitrate => {
                    int qualityLevel = bitrate switch {
                        >= 220 => 0,   // V0 (~220-260kbps avg)
                        >= 190 => 1,   // V1 (~190-250kbps)
                        >= 170 => 2,   // V2 (~170-210kbps)
                        >= 150 => 3,   // V3 (~150-195kbps)
                        >= 130 => 4,   // V4 (~130-175kbps)
                        >= 115 => 5,   // V5 (~115-155kbps)
                        >= 100 => 6,   // V6 (~100-140kbps)
                        >= 85 => 7,    // V7 (~85-125kbps)
                        >= 65 => 8,    // V8 (~65-105kbps)
                        _ => 9         // V9 (~45-85kbps)
                    };
                    return [$"-q:a {qualityLevel}"];
                }
            },

            {
                AudioFormat.Opus,
                bitrate => [$"-b:a {bitrate}k", "-compression_level 10"]
            },

            {
                AudioFormat.Vorbis,
                bitrate => [$"-q:a {AudioFormatHelper.MapBitrateToVorbisQuality(bitrate)}"]
            },

            { AudioFormat.MP4, bitrate => [$"-b:a {bitrate}k"] },
            {
                AudioFormat.OGG,
                bitrate => [$"-q:a {AudioFormatHelper.MapBitrateToVorbisQuality(bitrate)}"]
            },
            { AudioFormat.AMR, bitrate => [$"-ab {bitrate}k"]},
            { AudioFormat.WMA, bitrate => [$"-b:a {bitrate}k"]}
        };

        private static readonly Dictionary<AudioFormat, Func<int, string[]>> CBRQualityParameters = new()
        {
            {
                AudioFormat.MP3,
                bitrate => ["-b:a", $"{bitrate}k"]
            },
            {
                AudioFormat.AAC,
                bitrate => ["-b:a", $"{bitrate}k"]
            },
            {
                AudioFormat.Opus,
                bitrate => ["-b:a", $"{bitrate}k", "-vbr", "off"]
            },
            {
                AudioFormat.MP4,
                bitrate => ["-b:a", $"{bitrate}k"]
            },
            {
                AudioFormat.AMR,
                bitrate => ["-ab", $"{bitrate}k"]
            },
            {
                AudioFormat.WMA,
                bitrate => ["-b:a", $"{bitrate}k"]
            }
        };

        private static readonly Dictionary<AudioFormat, Func<int, string[]>> BitDepthParameters = new()
        {
            {
                AudioFormat.FLAC,
                bitDepth => bitDepth switch
                {
                    16 => ["-sample_fmt", "s16"],
                    24 => ["-sample_fmt", "s32", "-bits_per_raw_sample", "24"],
                    32 => ["-sample_fmt", "s32"],
                    _ => []
                }
            },
            {
                AudioFormat.WAV,
                bitDepth => bitDepth switch
                {
                    16 => ["-codec:a", "pcm_s16le"],
                    24 => ["-codec:a", "pcm_s24le"],
                    32 => ["-codec:a", "pcm_s32le"],
                    _ => []
                }
            },
            {
                AudioFormat.AIFF,
                bitDepth => bitDepth switch
                {
                    16 => ["-codec:a", "pcm_s16be"],
                    24 => ["-codec:a", "pcm_s24be"],
                    32 => ["-codec:a", "pcm_s32be"],
                    _ => []
                }
            }
        };

        private static readonly string[] ExtractionParameters =
        [
            "-codec:a copy",
            "-vn",
            "-movflags +faststart"
        ];

        private static readonly string[] VideoFormats =
        [
            "matroska", "webm",
            "mov", "mp4", "m4a",
            "avi",
            "asf", "wmv", "wma",
            "flv", "f4v",
            "3gp", "3g2",
            "mxf",
            "ts", "m2ts"
        ];

        private static readonly HashSet<string> CoverArtCodecs = new(StringComparer.OrdinalIgnoreCase)
        {
            "mjpeg", "png", "bmp", "gif", "webp", "jpeg", "jpg", "tiff", "tif"
        };

        private async Task<byte[]?> TryExtractCoverArtAsync()
        {
            try
            {
                using TagLib.File file = TagLib.File.Create(TrackPath);
                byte[]? data = file.Tag.Pictures?.FirstOrDefault()?.Data?.Data;
                if (data?.Length > 0)
                    return data;
            }
            catch (Exception ex)
            {
                _logger?.Trace(ex, "TagLib could not read embedded cover from {Path}; falling back to ffprobe", TrackPath);
            }

            try
            {
                IMediaInfo mediaInfo = await XabeFFmpeg.GetMediaInfo(TrackPath);
                IVideoStream? coverStream = mediaInfo.VideoStreams
                    .FirstOrDefault(vs => CoverArtCodecs.Contains(vs.Codec ?? ""));

                if (coverStream == null)
                    return null;

                string tempCoverPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.jpg");
                try
                {
                    IConversion conversion = XabeFFmpeg.Conversions.New()
                        .AddParameter($"-i \"{TrackPath}\"")
                        .AddParameter("-an -vcodec copy")
                        .SetOutput(tempCoverPath);

                    await conversion.Start();

                    if (File.Exists(tempCoverPath))
                        return await File.ReadAllBytesAsync(tempCoverPath);
                }
                finally
                {
                    if (File.Exists(tempCoverPath))
                        File.Delete(tempCoverPath);
                }
            }
            catch (Exception ex)
            {
                _logger?.Trace(ex, "ffprobe/ffmpeg cover extraction failed for {Path}", TrackPath);
            }

            return null;
        }

        /// <summary>
        /// Converts audio to the specified format with optional bitrate control.
        /// </summary>
        /// <param name="audioFormat">Target audio format</param>
        /// <param name="targetBitrate">Optional target bitrate in kbps</param>
        /// <returns>True if conversion succeeded, false otherwise</returns>
        public async Task<bool> TryConvertToFormatAsync(AudioFormat audioFormat, int? targetBitrate = null, int? targetBitDepth = null, bool useCBR = false)
        {
            _logger?.Trace($"Converting {Path.GetFileName(TrackPath)} to {audioFormat}" +
                          (targetBitrate.HasValue ? $" at {targetBitrate}kbps" :
                           targetBitDepth.HasValue ? $" at {targetBitDepth}-bit" : ""));

            if (!CheckFFmpegInstalled())
                return false;

            if (!await TryExtractAudioFromVideoAsync())
                return false;

            _logger?.Trace($"Looking up audio format: {audioFormat}");

            if (audioFormat == AudioFormat.Unknown)
                return true;

            if (!BaseConversionParameters.ContainsKey(audioFormat))
                return false;

            string finalOutputPath = Path.ChangeExtension(TrackPath, AudioFormatHelper.GetFileExtensionForFormat(audioFormat));
            string tempOutputPath = Path.ChangeExtension(TrackPath, $".converted{AudioFormatHelper.GetFileExtensionForFormat(audioFormat)}");

            try
            {
                if (File.Exists(tempOutputPath))
                    File.Delete(tempOutputPath);

                byte[]? preservedCoverArt = AlbumCover?.Length > 0 ? AlbumCover : await TryExtractCoverArtAsync();

                IConversion conversion = await XabeFFmpeg.Conversions.FromSnippet.Convert(TrackPath, tempOutputPath);

                foreach (string parameter in BaseConversionParameters[audioFormat])
                    conversion.AddParameter(parameter);

                if (AudioFormatHelper.IsLossyFormat(audioFormat))
                {
                    int bitrate = targetBitrate ?? AudioFormatHelper.GetDefaultBitrate(audioFormat);
                    bitrate = AudioFormatHelper.ClampBitrate(audioFormat, bitrate);

                    string[] qualityParams;
                    string mode;

                    if (useCBR && CBRQualityParameters.ContainsKey(audioFormat))
                    {
                        qualityParams = CBRQualityParameters[audioFormat](bitrate);
                        mode = "CBR";
                    }
                    else if (QualityParameters.ContainsKey(audioFormat))
                    {
                        qualityParams = QualityParameters[audioFormat](bitrate);
                        mode = "VBR";
                    }
                    else
                    {
                        qualityParams = [$"-b:a {bitrate}k"];
                        mode = "fallback";
                    }

                    foreach (string param in qualityParams)
                        conversion.AddParameter(param);

                    _logger?.Trace($"Applied {mode} quality parameters for {audioFormat} at {bitrate}kbps: {string.Join(", ", qualityParams)}");
                }

                if (!AudioFormatHelper.IsLossyFormat(audioFormat) &&
                    BitDepthParameters.ContainsKey(audioFormat) &&
                    targetBitDepth.HasValue)
                {
                    string[] bitDepthParams = BitDepthParameters[audioFormat](targetBitDepth.Value);
                    foreach (string param in bitDepthParams)
                        conversion.AddParameter(param);

                    _logger?.Trace($"Applied bit depth parameters for {audioFormat}: {targetBitDepth}-bit ({string.Join(", ", bitDepthParams)})");
                }

                _logger?.Trace($"Starting FFmpeg conversion");
                await conversion.Start();

                // Verify the converter actually produced a decodable file before
                // we delete the original. A successful Xabe/ffmpeg exit only means
                // the process returned 0 — it does not prove the muxer wrote a
                // valid stream. Catching this here is the difference between
                // "scan said clean" and "external tools later report Header
                // missing": the converter is the LAST thing to touch the file
                // before Lidarr finalises the import.
                if (!File.Exists(tempOutputPath) || new FileInfo(tempOutputPath).Length < 1024)
                {
                    _logger?.Error("FFmpeg convert: output {Path} is missing or too small after conversion ({Format} target). Leaving original in place.", tempOutputPath, audioFormat);
                    if (File.Exists(tempOutputPath))
                        try { File.Delete(tempOutputPath); } catch (Exception delEx) { _logger?.Trace(delEx, "Could not delete bad temp output {Path}", tempOutputPath); }
                    return false;
                }

                (int verifyExit, string verifyStderr) = await CorruptionScanner.DecodeCheckAsync(
                    tempOutputPath,
                    timeoutSeconds: 120,
                    ct: CancellationToken.None);
                if (verifyExit != 0)
                {
                    string reason = verifyExit == -1
                        ? "decode timed out"
                        : FfmpegErrorFormatter.CleanFfmpegErrors(verifyStderr);
                    _logger?.Error("FFmpeg convert: output {Path} failed post-conversion decode (exit={ExitCode}) — {Reason}. Leaving original in place.", tempOutputPath, verifyExit, reason);
                    try { File.Delete(tempOutputPath); } catch (Exception delEx) { _logger?.Trace(delEx, "Could not delete bad temp output {Path}", tempOutputPath); }
                    return false;
                }

                if (File.Exists(TrackPath))
                    File.Delete(TrackPath);

                File.Move(tempOutputPath, finalOutputPath, true);
                TrackPath = finalOutputPath;

                if (preservedCoverArt?.Length > 0)
                {
                    try
                    {
                        using TagLib.File destFile = TagLib.File.Create(TrackPath);
                        destFile.Tag.Pictures = [new TagLib.Picture(new TagLib.ByteVector(preservedCoverArt))
                        {
                            Type = TagLib.PictureType.FrontCover,
                            Description = "Album Cover"
                        }];
                        destFile.Save();
                        _logger?.Trace("Re-embedded cover art into converted file");
                    }
                    catch (Exception ex)
                    {
                        _logger?.Warn(ex, "Failed to re-embed cover art after conversion, cover art may be missing");
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to convert file to {audioFormat}: {TrackPath}");
                return false;
            }
        }

        public async Task<bool> IsVideoContainerAsync()
        {
            try
            {
                IMediaInfo mediaInfo = await XabeFFmpeg.GetMediaInfo(TrackPath);

                bool hasRealVideo = mediaInfo.VideoStreams.Any(vs =>
                    !CoverArtCodecs.Contains(vs.Codec ?? "") &&
                    !(vs.Duration.TotalSeconds < 1 && vs.Framerate <= 1));

                if (hasRealVideo)
                    return true;

                string probeResult = await Probe.New().Start($"-v error -show_entries format=format_name -of default=noprint_wrappers=1:nokey=1 \"{TrackPath}\"");
                string formatName = probeResult?.Trim().ToLower() ?? "";
                return VideoFormats.Any(container => formatName.Contains(container));
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to check file header: {TrackPath}");
                return false;
            }
        }

        public async Task<bool> TryExtractAudioFromVideoAsync()
        {
            if (!CheckFFmpegInstalled())
                return false;

            bool isVideo = await IsVideoContainerAsync();
            if (!isVideo)
                return await EnsureFileExtAsync();

            _logger?.Trace($"Extracting audio from video file: {Path.GetFileName(TrackPath)}");

            try
            {
                IMediaInfo mediaInfo = await XabeFFmpeg.GetMediaInfo(TrackPath);
                IAudioStream? audioStream = mediaInfo.AudioStreams.FirstOrDefault();

                if (audioStream == null)
                {
                    _logger?.Trace("No audio stream found in video file");
                    return false;
                }

                string codec = audioStream.Codec.ToLower();
                string finalOutputPath = Path.ChangeExtension(TrackPath, AudioFormatHelper.GetFileExtensionForCodec(codec));
                string tempOutputPath = Path.ChangeExtension(TrackPath, $".extracted{AudioFormatHelper.GetFileExtensionForCodec(codec)}");

                if (File.Exists(tempOutputPath))
                    File.Delete(tempOutputPath);

                IConversion conversion = await XabeFFmpeg.Conversions.FromSnippet.ExtractAudio(TrackPath, tempOutputPath);
                foreach (string parameter in ExtractionParameters)
                    conversion.AddParameter(parameter);

                await conversion.Start();

                if (File.Exists(TrackPath))
                    File.Delete(TrackPath);

                File.Move(tempOutputPath, finalOutputPath, true);
                TrackPath = finalOutputPath;
                await EnsureFileExtAsync();

                _logger?.Trace($"Successfully extracted audio to {Path.GetFileName(TrackPath)}");
                return true;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to extract audio from video: {TrackPath}");
                return false;
            }
        }


        /// <summary>
        /// Decrypts an encrypted audio file using FFmpeg with the provided decryption key.
        /// </summary>
        /// <param name="decryptionKey">The hex decryption key for the encrypted content.</param>
        /// <param name="codec">The audio codec of the content (e.g., "flac", "opus", "eac3").</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True if decryption was successful, false otherwise.</returns>
        public async Task<bool> TryDecryptAsync(string decryptionKey, string? codec, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(decryptionKey))
                return true;

            if (!CheckFFmpegInstalled())
                return false;

            _logger?.Trace($"Decrypting file: {Path.GetFileName(TrackPath)}");

            try
            {
                AudioFormat format = AudioFormatHelper.GetAudioFormatFromCodec(codec ?? "aac");
                string extension = AudioFormatHelper.GetFileExtensionForFormat(format);
                string outputPath = Path.ChangeExtension(TrackPath, extension);
                string tempOutput = Path.ChangeExtension(TrackPath, $".dec{extension}");

                if (File.Exists(tempOutput))
                    File.Delete(tempOutput);

                IConversion conversion = XabeFFmpeg.Conversions.New()
                    .AddParameter($"-decryption_key {decryptionKey}")
                    .AddParameter($"-i \"{TrackPath}\"")
                    .AddParameter("-c copy")
                    .SetOutput(tempOutput);

                await conversion.Start(token);

                if (File.Exists(TrackPath))
                    File.Delete(TrackPath);

                File.Move(tempOutput, outputPath, true);
                TrackPath = outputPath;

                _logger?.Trace($"Successfully decrypted: {Path.GetFileName(TrackPath)}");
                return true;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to decrypt file: {TrackPath}");
                return false;
            }
        }

        public async Task<bool> TryCreateLrcFileAsync(CancellationToken token)
        {
            if (Lyric?.SyncedLyrics == null)
                return false;
            try
            {
                string lrcContent = string.Join(Environment.NewLine, Lyric.SyncedLyrics
                    .Where(lyric => !string.IsNullOrEmpty(lyric.LrcTimestamp) && !string.IsNullOrEmpty(lyric.Line))
                    .Select(lyric => $"{lyric.LrcTimestamp} {lyric.Line}"));

                string lrcPath = Path.ChangeExtension(TrackPath, ".lrc");
                await File.WriteAllTextAsync(lrcPath, lrcContent, token);
                _logger?.Trace($"Created LRC file with {Lyric.SyncedLyrics.Count} synced lyrics");
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to create LRC file: {Path.ChangeExtension(TrackPath, ".lrc")}");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Ensures the file extension matches the actual audio codec.
        /// </summary>
        /// <returns>True if the file extension is correct or was successfully corrected; otherwise, false.</returns>
        public async Task<bool> EnsureFileExtAsync()
        {
            try
            {
                IMediaInfo mediaInfo = await XabeFFmpeg.GetMediaInfo(TrackPath);
                string codec = mediaInfo.AudioStreams.FirstOrDefault()?.Codec.ToLower() ?? string.Empty;
                if (string.IsNullOrEmpty(codec))
                    return false;

                // Strict lookup: don't guess. The previous behaviour defaulted to
                // .aac for any unrecognised codec, which means a build of ffmpeg
                // that reports a codec name we don't list (or a future codec)
                // would silently rename the user's .mp3 to .aac and break import.
                if (!AudioFormatHelper.TryGetFileExtensionForCodec(codec, out string correctExtension))
                {
                    _logger?.Trace("EnsureFileExt: codec {Codec} not in known map; leaving {Path} alone", codec, TrackPath);
                    return true;
                }

                string currentExtension = Path.GetExtension(TrackPath);

                if (!string.Equals(currentExtension, correctExtension, StringComparison.OrdinalIgnoreCase))
                {
                    string newPath = Path.ChangeExtension(TrackPath, correctExtension);
                    _logger?.Trace($"Correcting file extension from {currentExtension} to {correctExtension} for codec {codec}");
                    File.Move(TrackPath, newPath);
                    TrackPath = newPath;
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to ensure correct file extension: {TrackPath}");
                return false;
            }
        }

        public bool TryEmbedMetadata(Album albumInfo, Track trackInfo, IAudioTagService tagService)
        {
            _logger?.Trace($"Embedding metadata for track: {trackInfo?.Title}");

            if (albumInfo == null || trackInfo == null || tagService == null)
            {
                _logger?.Warn($"Cannot embed metadata: album/track/service is null for {TrackPath}");
                return false;
            }

            try
            {
                TrackFile transient = new()
                {
                    Path = TrackPath,
                    AlbumId = albumInfo.Id,
                    Album = albumInfo,
                    Artist = albumInfo.Artist?.Value!,
                    Tracks = new LazyLoaded<List<Track>>(new List<Track> { trackInfo })
                };

                tagService.WriteTags(transient, newDownload: true, force: true);
                return true;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Lidarr tagging failed for {TrackPath}");
                return false;
            }
        }

        /// <summary>
        /// Checks if the specified audio format is supported for encoding by FFmpeg.
        /// </summary>
        /// <param name="format">The audio format to check</param>
        /// <returns>True if the format can be used as a conversion target, false otherwise</returns>
        public static bool IsTargetFormatSupportedForEncoding(AudioFormat format) => BaseConversionParameters.ContainsKey(format);

        ///// <summary>
        ///// Checks if a given audio format supports embedded metadata tags.
        ///// </summary>
        ///// <param name="format">The audio format to check</param>
        ///// <returns>True if the format supports metadata tagging, false otherwise</returns>
        public static bool SupportsMetadataEmbedding(AudioFormat format) => format switch
        {
            // Formats that DO NOT support metadata embedding
            AudioFormat.AC3 or AudioFormat.EAC3 or AudioFormat.MIDI => false,

            // Formats that DO support metadata embedding
            AudioFormat.AAC or AudioFormat.MP3 or AudioFormat.Opus or AudioFormat.Vorbis or
            AudioFormat.FLAC or AudioFormat.WAV or AudioFormat.MP4 or AudioFormat.AIFF or
            AudioFormat.OGG or AudioFormat.WMA or AudioFormat.ALAC or AudioFormat.APE => true,

            // Unknown formats - assume they might support it
            _ => true
        };

        /// <summary>
        /// Gets the actual audio codec from a file using FFmpeg and returns the corresponding AudioFormat.
        /// </summary>
        /// <param name="filePath">Path to the audio file</param>
        /// <returns>AudioFormat enum value or AudioFormat.Unknown if codec is not supported or detection fails</returns>
        public static async Task<AudioFormat> GetSupportedCodecAsync(string filePath)
        {
            try
            {
                IMediaInfo mediaInfo = await XabeFFmpeg.GetMediaInfo(filePath);
                IAudioStream? audioStream = mediaInfo.AudioStreams.FirstOrDefault();

                if (audioStream == null)
                {
                    NzbDroneLogger.GetLogger(typeof(AudioMetadataHandler)).Debug("No audio stream found in file: {0}", filePath);
                    return AudioFormat.Unknown;
                }

                string codec = audioStream.Codec.ToLower();
                AudioFormat format = AudioFormatHelper.GetAudioFormatFromCodec(codec);

                NzbDroneLogger.GetLogger(typeof(AudioMetadataHandler)).Trace("Detected codec '{0}' as format '{1}' for file: {2}", codec, format, filePath);
                return format;
            }
            catch (Exception ex)
            {
                NzbDroneLogger.GetLogger(typeof(AudioMetadataHandler)).Error(ex, "Failed to detect codec for file: {0}", filePath);
                return AudioFormat.Unknown;
            }
        }

        public static bool CheckFFmpegInstalled()
        {
            if (_isFFmpegInstalled.HasValue)
                return _isFFmpegInstalled.Value;

            // Resolve every candidate (configured path, $FFMPEG, $PATH), probe each
            // binary's version, and pick the newest. The previous code used the first
            // candidate found, which meant a Xabe-auto-installed ffmpeg 4.4.1
            // (ffbinaries.com is frozen at that release) at the user-configured path
            // would win over the container's system ffmpeg 5.x/6.x — and silently
            // accept malformed MP3 framing that newer builds correctly flag.
            List<string> candidates = new();

            if (!string.IsNullOrEmpty(XabeFFmpeg.ExecutablesPath) && Directory.Exists(XabeFFmpeg.ExecutablesPath))
                candidates.AddRange(EnumerateFfmpegBinaries(XabeFFmpeg.ExecutablesPath));

            string? ffmpegEnv = Environment.GetEnvironmentVariable("FFMPEG");
            if (!string.IsNullOrEmpty(ffmpegEnv))
            {
                string envDir = File.Exists(ffmpegEnv) ? Path.GetDirectoryName(ffmpegEnv)! : ffmpegEnv;
                if (Directory.Exists(envDir))
                    candidates.AddRange(EnumerateFfmpegBinaries(envDir));
            }

            foreach (string path in Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [])
            {
                if (Directory.Exists(path))
                    candidates.AddRange(EnumerateFfmpegBinaries(path));
            }

            // Deduplicate (the same dir can appear in multiple sources).
            candidates = candidates
                .Select(p => Path.GetFullPath(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (candidates.Count == 0)
            {
                NzbDroneLogger.GetLogger(typeof(AudioMetadataHandler)).Trace("FFmpeg not found in configured path or system PATH");
                _isFFmpegInstalled = false;
                return false;
            }

            (string Path, Version Version)? best = null;
            foreach (string candidate in candidates)
            {
                Version? v = ProbeFfmpegVersion(candidate);
                if (v == null)
                    continue;
                if (best == null || v > best.Value.Version)
                    best = (candidate, v);
            }

            if (best == null)
            {
                // Found binaries but none responded to -version. Fall back to the
                // first candidate with a passing magic-number check rather than
                // failing closed; this preserves prior behaviour for exotic builds.
                string fallback = candidates.First();
                XabeFFmpeg.SetExecutablesPath(Path.GetDirectoryName(fallback)!);
                NzbDroneLogger.GetLogger(typeof(AudioMetadataHandler)).Warn("FFmpeg version probe failed for all {Count} candidate(s); using {Path} without version verification.", candidates.Count, fallback);
                _isFFmpegInstalled = true;
                return true;
            }

            XabeFFmpeg.SetExecutablesPath(Path.GetDirectoryName(best.Value.Path)!);
            Logger logger = NzbDroneLogger.GetLogger(typeof(AudioMetadataHandler));
            logger.Info("FFmpeg selected: {Path} (version {Version}) out of {Count} candidate(s).", best.Value.Path, best.Value.Version, candidates.Count);
            if (best.Value.Version.Major < 5)
                logger.Warn("FFmpeg {Version} is older than 5.x. Older ffmpeg builds silently accept malformed MP3 framing that newer builds correctly flag as corrupt — consider installing a newer ffmpeg on the host or in the container.", best.Value.Version);

            _isFFmpegInstalled = true;
            return true;
        }

        private static IEnumerable<string> EnumerateFfmpegBinaries(string dir)
        {
            string[] ffmpegPatterns = ["ffmpeg", "ffmpeg.exe", "ffmpeg.bin"];
            string[] files;
            try
            {
                files = Directory.GetFiles(dir);
            }
            catch (Exception ex)
            {
                NzbDroneLogger.GetLogger(typeof(AudioMetadataHandler)).Trace(ex, "Failed to enumerate FFmpeg candidates in {Dir}", dir);
                yield break;
            }

            foreach (string file in files)
            {
                if (ffmpegPatterns.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase) && IsExecutable(file))
                    yield return file;
            }
        }

        private static Version? ProbeFfmpegVersion(string ffmpegPath)
        {
            try
            {
                System.Diagnostics.ProcessStartInfo psi = new()
                {
                    FileName = ffmpegPath,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("-hide_banner");
                psi.ArgumentList.Add("-version");

                using System.Diagnostics.Process proc = new() { StartInfo = psi };
                proc.Start();
                string stdout = proc.StandardOutput.ReadToEnd();
                _ = proc.StandardError.ReadToEnd();
                if (!proc.WaitForExit(milliseconds: 5000))
                {
                    try { proc.Kill(entireProcessTree: true); } catch (Exception killEx) { NzbDroneLogger.GetLogger(typeof(AudioMetadataHandler)).Trace(killEx, "Failed to kill stuck ffmpeg version probe at {Path}", ffmpegPath); }
                    return null;
                }

                // First line: "ffmpeg version 6.1.1-3ubuntu5 Copyright ..."
                string firstLine = (stdout ?? string.Empty).Split('\n', 2)[0];
                System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(
                    firstLine,
                    @"ffmpeg\s+version\s+(?<ver>\d+(?:\.\d+){1,3})",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!m.Success)
                    return null;
                if (!Version.TryParse(m.Groups["ver"].Value, out Version? parsed))
                    return null;
                return parsed;
            }
            catch (Exception ex)
            {
                NzbDroneLogger.GetLogger(typeof(AudioMetadataHandler)).Trace(ex, "FFmpeg version probe failed for {Path}", ffmpegPath);
                return null;
            }
        }

        private static bool IsExecutable(string filePath)
        {
            try
            {
                using FileStream stream = File.OpenRead(filePath);
                byte[] magicNumber = new byte[4];
                stream.Read(magicNumber, 0, 4);

                // Windows PE
                if (magicNumber[0] == 0x4D && magicNumber[1] == 0x5A)
                    return true;

                // Linux ELF
                if (magicNumber[0] == 0x7F && magicNumber[1] == 0x45 &&
                    magicNumber[2] == 0x4C && magicNumber[3] == 0x46)
                    return true;

                // macOS Mach-O (32-bit: 0xFEEDFACE, 64-bit: 0xFEEDFACF)
                if (magicNumber[0] == 0xFE && magicNumber[1] == 0xED &&
                    magicNumber[2] == 0xFA &&
                    (magicNumber[3] == 0xCE || magicNumber[3] == 0xCF))
                    return true;

                // Universal Binary (fat_header)
                if (magicNumber[0] == 0xCA && magicNumber[1] == 0xFE &&
                    magicNumber[2] == 0xBA && magicNumber[3] == 0xBE)
                    return true;
            }
            catch (Exception ex)
            {
                // Read failed (perm denied, broken symlink, race) — treat as not
                // executable. Trace because this fires per candidate during the
                // ffmpeg-binary search and would be noisy at higher levels.
                NzbDroneLogger.GetLogger(typeof(AudioMetadataHandler))
                    .Trace(ex, "IsExecutable: failed to read magic bytes from {Path}", filePath);
            }
            return false;
        }

        public static void ResetFFmpegInstallationCheck() => _isFFmpegInstalled = null;

        /// <summary>
        /// Default deadline applied to <see cref="InstallFFmpeg(string)"/> when callers don't
        /// supply one. Xabe.FFmpeg.Downloader has no timeout itself; without a deadline a
        /// stalled HTTPS connection or hung archive extraction blocks indefinitely on the
        /// thread that called <c>.GetAwaiter().GetResult()</c> (Tidal post-process thread,
        /// the FFmpeg metadata "Test" button save). 5 minutes is generous enough for a
        /// real download on a slow connection but still recovers from a hang.
        /// </summary>
        public static readonly TimeSpan DefaultInstallDeadline = TimeSpan.FromMinutes(5);

        public static async Task InstallFFmpeg(string path, CancellationToken ct = default)
        {
            NzbDroneLogger.GetLogger(typeof(AudioMetadataHandler)).Trace($"Installing FFmpeg to: {path}");
            ResetFFmpegInstallationCheck();
            XabeFFmpeg.SetExecutablesPath(path);
            if (CheckFFmpegInstalled())
                return;

            // FFmpegDownloader.GetLatestVersion accepts no CancellationToken, so we
            // race it against a Task.Delay deadline and bail loudly on timeout
            // rather than blocking the caller forever.
            using CancellationTokenSource deadline = ct.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : new CancellationTokenSource();
            deadline.CancelAfter(DefaultInstallDeadline);

            Task download = FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official, path);
            Task delay = Task.Delay(Timeout.Infinite, deadline.Token);
            Task winner = await Task.WhenAny(download, delay).ConfigureAwait(false);
            if (winner == delay)
            {
                throw new TimeoutException(
                    $"FFmpeg auto-install at {path} did not complete within {DefaultInstallDeadline.TotalMinutes:F0} min — bailing out so the caller is not blocked.");
            }

            // Surface any download exception to the caller.
            await download.ConfigureAwait(false);
        }
    }
}