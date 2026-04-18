using System.Text;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.TrackImport;
using NzbDrone.Core.MediaFiles.TrackImport.Identification;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Plugin.Sleezer.Core.PostProcessing;

public interface IPreImportTagger
{
    Task<PreImportTagger.TaggingResult> TagCompletedDownloadAsync(
        Album album,
        Artist artist,
        AlbumRelease? albumRelease,
        string sourceId,
        string completedFolderPath,
        double confidenceThreshold,
        bool stripFeaturedArtists,
        CancellationToken ct);
}

public class PreImportTagger : IPreImportTagger
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

    public PreImportTagger(
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
        Album album,
        Artist artist,
        AlbumRelease? albumRelease,
        string sourceId,
        string completedFolderPath,
        double confidenceThreshold,
        bool stripFeaturedArtists,
        CancellationToken ct)
    {
        try
        {
            TaggingResult result = TagInternal(album, artist, albumRelease, sourceId, completedFolderPath, confidenceThreshold, stripFeaturedArtists, ct);
            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"Pre-import tagging failed for {sourceId}");
            return Task.FromResult(new TaggingResult(0, 0, 1));
        }
    }

    private TaggingResult TagInternal(
        Album album,
        Artist artist,
        AlbumRelease? albumRelease,
        string sourceId,
        string folderPath,
        double confidenceThreshold,
        bool stripFeaturedArtists,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!_diskProvider.FolderExists(folderPath))
        {
            _logger.Warn($"Pre-import tag: folder does not exist: {folderPath}");
            return new TaggingResult(0, 0, 0);
        }

        // Caller may pass null albumRelease — fall back to the album's monitored
        // release (or first available) so callers don't all have to repeat that.
        albumRelease ??= album.AlbumReleases?.Value?.FirstOrDefault(r => r.Monitored)
                         ?? album.AlbumReleases?.Value?.FirstOrDefault();

        List<string> audioFiles = EnumerateAudioFiles(folderPath);
        if (audioFiles.Count == 0)
        {
            _logger.Debug($"Pre-import tag: no audio files found under {folderPath}");
            return new TaggingResult(0, 0, 0);
        }

        List<LocalTrack> localTracks = audioFiles.Select(path =>
        {
            ParsedTrackInfo info = SafeReadTags(path);
            if (stripFeaturedArtists)
            {
                // Pre-clean the tag-derived title before Identify so a tag like
                // "Foo (feat. Bar)" still matches a catalog track named "Foo".
                info.Title = FeaturedArtistStripper.Strip(info.Title);
                info.CleanTitle = FeaturedArtistStripper.Strip(info.CleanTitle);
                info.ArtistTitle = FeaturedArtistStripper.Strip(info.ArtistTitle);
            }

            return new LocalTrack
            {
                Path = path,
                Size = SafeFileSize(path),
                Modified = SafeFileModified(path),
                Quality = new QualityModel(Quality.Unknown),
                // Lidarr's AggregateFilenameInfo + CandidateService dereference
                // FileTrackInfo.Title / .ReleaseMBId / .TrackNumbers, so it has to
                // be populated before Identify runs. This mirrors what
                // ImportDecisionMaker.GetLocalTracks does in the normal flow.
                FileTrackInfo = info
            };
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
                _logger.Info($"Pre-import tag: album match confidence {albumDistance:F3} above threshold {confidenceThreshold:F3} — skipping tagging for {release.LocalTracks.Count} files in {sourceId}");
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

                if (TryTagSingleFile(localTrack, track, album, release.AlbumRelease, stripFeaturedArtists))
                    tagged++;
                else
                    errored++;
            }
        }

        _logger.Info($"Pre-import tag: {sourceId} tagged={tagged} skipped_low_confidence={skipped} errored={errored}");
        return new TaggingResult(tagged, skipped, errored);
    }

    private bool TryTagSingleFile(LocalTrack localTrack, Track track, Album album, AlbumRelease? albumRelease, bool stripFeaturedArtists)
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

            if (stripFeaturedArtists)
                ApplyFeaturedArtistCleanup(transient, track);

            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn(ex, $"Pre-import tag: write failed for {localTrack.Path}");
            return false;
        }
    }

    /// <summary>
    /// After Lidarr's tag writer runs, re-open the file with TagLib and strip
    /// bracketed `(feat. X)` suffixes from Title / Performers / AlbumArtists.
    /// Then rename the file from the cleaned title so Lidarr's importer sees
    /// the clean basename. Best-effort: any failure is logged and swallowed
    /// so the parent tag-write still counts as a success.
    /// </summary>
    private void ApplyFeaturedArtistCleanup(TrackFile transient, Track track)
    {
        string path = transient.Path;
        try
        {
            using (TagLib.File file = TagLib.File.Create(path))
            {
                file.Tag.Title = FeaturedArtistStripper.Strip(file.Tag.Title);
                file.Tag.Performers = file.Tag.Performers.Select(p => FeaturedArtistStripper.Strip(p)).ToArray();
                file.Tag.AlbumArtists = file.Tag.AlbumArtists.Select(a => FeaturedArtistStripper.Strip(a)).ToArray();
                file.Save();
            }
        }
        catch (Exception ex)
        {
            _logger.Warn(ex, $"Pre-import tag: feat-strip tag rewrite failed for {path}");
            return;
        }

        try
        {
            string? dir = Path.GetDirectoryName(path);
            string ext = Path.GetExtension(path);
            string cleanTitle = FeaturedArtistStripper.Strip(track.Title);
            if (string.IsNullOrWhiteSpace(cleanTitle) || string.IsNullOrEmpty(dir))
                return;

            int trackNumber = track.AbsoluteTrackNumber;
            string baseName = trackNumber > 0
                ? $"{trackNumber:D2} - {SanitiseForFilename(cleanTitle)}{ext}"
                : $"{SanitiseForFilename(cleanTitle)}{ext}";

            string newPath = Path.Combine(dir, baseName);
            if (string.Equals(newPath, path, StringComparison.OrdinalIgnoreCase))
                return;
            if (File.Exists(newPath))
            {
                _logger.Trace($"Pre-import tag: feat-strip rename target already exists, skipping: {newPath}");
                return;
            }

            File.Move(path, newPath);
            transient.Path = newPath;
        }
        catch (Exception ex)
        {
            _logger.Warn(ex, $"Pre-import tag: feat-strip rename failed for {path}");
        }
    }

    private static readonly char[] InvalidFilenameChars = Path.GetInvalidFileNameChars();
    private static string SanitiseForFilename(string input)
    {
        StringBuilder sb = new(input.Length);
        foreach (char c in input)
            sb.Append(InvalidFilenameChars.Contains(c) ? '_' : c);
        return sb.ToString().Trim();
    }

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

    private ParsedTrackInfo SafeReadTags(string path)
    {
        try
        {
            ParsedTrackInfo? info = _audioTagService.ReadTags(path);
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
        return new ParsedTrackInfo
        {
            TrackNumbers = new[] { 0 }
        };
    }
}
