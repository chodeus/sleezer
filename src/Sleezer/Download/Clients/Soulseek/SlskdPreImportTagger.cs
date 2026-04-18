using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.TrackImport;
using NzbDrone.Core.MediaFiles.TrackImport.Identification;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;

public interface ISlskdPreImportTagger
{
    Task<SlskdPreImportTagger.TaggingResult> TagCompletedDownloadAsync(
        SlskdDownloadItem item,
        string completedFolderPath,
        double confidenceThreshold,
        CancellationToken ct);
}

public class SlskdPreImportTagger : ISlskdPreImportTagger
{
    private static readonly string[] AudioExtensions =
    [
        ".flac", ".mp3", ".m4a", ".ogg", ".opus", ".wav",
        ".wma", ".aac", ".aiff", ".aif", ".ape", ".wv",
        ".alac", ".m4b", ".m4p", ".mp2", ".mpc", ".dsf", ".dff"
    ];

    private readonly IIdentificationService _identificationService;
    private readonly IAudioTagService _audioTagService;
    private readonly IDiskProvider _diskProvider;
    private readonly Logger _logger;

    public SlskdPreImportTagger(
        IIdentificationService identificationService,
        IAudioTagService audioTagService,
        IDiskProvider diskProvider,
        Logger logger)
    {
        _identificationService = identificationService;
        _audioTagService = audioTagService;
        _diskProvider = diskProvider;
        _logger = logger;
    }

    public record TaggingResult(int Tagged, int SkippedLowConfidence, int Errored);

    public Task<TaggingResult> TagCompletedDownloadAsync(
        SlskdDownloadItem item,
        string completedFolderPath,
        double confidenceThreshold,
        CancellationToken ct)
    {
        try
        {
            TaggingResult result = TagInternal(item, completedFolderPath, confidenceThreshold, ct);
            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"Pre-import tagging failed for {item.ID}");
            return Task.FromResult(new TaggingResult(0, 0, 1));
        }
    }

    private TaggingResult TagInternal(SlskdDownloadItem item, string folderPath, double confidenceThreshold, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!_diskProvider.FolderExists(folderPath))
        {
            _logger.Warn($"Pre-import tag: folder does not exist: {folderPath}");
            return new TaggingResult(0, 0, 0);
        }

        Album? album = ResolveAlbum(item);
        Artist? artist = album?.Artist?.Value;
        AlbumRelease? albumRelease = album?.AlbumReleases?.Value?.FirstOrDefault(r => r.Monitored)
                                     ?? album?.AlbumReleases?.Value?.FirstOrDefault();

        if (album == null || artist == null)
        {
            _logger.Debug($"Pre-import tag: skipping {item.ID}; missing Album/Artist on ReleaseInfo. Lidarr's importer will tag later if configured.");
            return new TaggingResult(0, 0, 0);
        }

        List<string> audioFiles = EnumerateAudioFiles(folderPath);
        if (audioFiles.Count == 0)
        {
            _logger.Debug($"Pre-import tag: no audio files found under {folderPath}");
            return new TaggingResult(0, 0, 0);
        }

        List<LocalTrack> localTracks = audioFiles.Select(path => new LocalTrack
        {
            Path = path,
            Size = SafeFileSize(path),
            Modified = SafeFileModified(path),
            Quality = new QualityModel(Quality.Unknown),
            // Lidarr's AggregateFilenameInfo + CandidateService dereference
            // FileTrackInfo.Title / .ReleaseMBId / .TrackNumbers, so it has to
            // be populated before Identify runs. This mirrors what
            // ImportDecisionMaker.GetLocalTracks does in the normal flow.
            FileTrackInfo = SafeReadTags(path)
        }).ToList();

        IdentificationOverrides overrides = new()
        {
            Artist = artist,
            Album = album
        };
        if (albumRelease != null)
            overrides.AlbumRelease = albumRelease;

        ImportDecisionMakerConfig config = new()
        {
            NewDownload = true,
            SingleRelease = true,
            IncludeExisting = false,
            AddNewArtists = false
        };

        List<LocalAlbumRelease> releases = _identificationService.Identify(localTracks, overrides, config);

        int tagged = 0;
        int skipped = 0;
        int errored = 0;

        foreach (LocalAlbumRelease release in releases)
        {
            double albumDistance = release.Distance?.NormalizedDistance() ?? 1.0;
            if (albumDistance > confidenceThreshold || release.TrackMapping == null)
            {
                skipped += release.LocalTracks.Count;
                _logger.Info($"Pre-import tag: album match confidence {albumDistance:F3} above threshold {confidenceThreshold:F3} — skipping tagging for {release.LocalTracks.Count} files in {item.ID}");
                continue;
            }

            release.AlbumRelease ??= albumRelease;

            foreach (KeyValuePair<LocalTrack, Tuple<Track, Distance>> entry in release.TrackMapping.Mapping)
            {
                ct.ThrowIfCancellationRequested();

                LocalTrack localTrack = entry.Key;
                Track track = entry.Value.Item1;
                double trackDistance = entry.Value.Item2?.NormalizedDistance() ?? 1.0;

                if (trackDistance > confidenceThreshold)
                {
                    skipped++;
                    _logger.Trace($"Pre-import tag: track match {trackDistance:F3} above threshold; skipping {Path.GetFileName(localTrack.Path)}");
                    continue;
                }

                if (TryTagSingleFile(localTrack, track, album, release.AlbumRelease))
                    tagged++;
                else
                    errored++;
            }
        }

        _logger.Info($"Pre-import tag: {item.ID} tagged={tagged} skipped_low_confidence={skipped} errored={errored}");
        return new TaggingResult(tagged, skipped, errored);
    }

    private bool TryTagSingleFile(LocalTrack localTrack, Track track, Album album, AlbumRelease? albumRelease)
    {
        try
        {
            if (albumRelease != null && track.AlbumRelease == null)
                track.AlbumRelease = albumRelease;

            TrackFile transient = new()
            {
                Path = localTrack.Path,
                AlbumId = album.Id,
                Album = album,
                Artist = album.Artist?.Value!,
                Tracks = new LazyLoaded<List<Track>>(new List<Track> { track }),
                Quality = localTrack.Quality ?? new QualityModel(Quality.Unknown),
                Size = localTrack.Size
            };

            _audioTagService.WriteTags(transient, newDownload: true, force: true);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn(ex, $"Pre-import tag: write failed for {localTrack.Path}");
            return false;
        }
    }

    private static Album? ResolveAlbum(SlskdDownloadItem item) => item.ResolvedAlbum;

    private List<string> EnumerateAudioFiles(string folderPath)
    {
        List<string> result = new();
        try
        {
            foreach (string file in _diskProvider.GetFiles(folderPath, recursive: true))
            {
                string ext = Path.GetExtension(file);
                if (AudioExtensions.Any(e => string.Equals(ext, e, StringComparison.OrdinalIgnoreCase)))
                    result.Add(file);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn(ex, $"Failed to enumerate audio files under {folderPath}");
        }
        return result;
    }

    private long SafeFileSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    private DateTime SafeFileModified(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.UtcNow; }
    }

    private NzbDrone.Core.Parser.Model.ParsedTrackInfo SafeReadTags(string path)
    {
        try
        {
            NzbDrone.Core.Parser.Model.ParsedTrackInfo? info = _audioTagService.ReadTags(path);
            if (info != null)
                return info;
        }
        catch (Exception ex)
        {
            _logger.Warn(ex, $"Pre-import tag: could not read tags from {path}; using empty placeholder");
        }

        // Lidarr's AggregateFilenameInfo does TrackNumbers.First() == 0 which
        // throws on an empty array. Seed with [0] so the "missing data" branch
        // fires gracefully instead.
        return new NzbDrone.Core.Parser.Model.ParsedTrackInfo
        {
            TrackNumbers = new[] { 0 }
        };
    }
}
