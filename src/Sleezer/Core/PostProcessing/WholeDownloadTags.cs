using NLog;
using NzbDrone.Core.MediaFiles.TrackImport.Identification;
using NzbDrone.Core.MediaFiles.TrackImport.Specifications;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Plugin.Sleezer.Core.PostProcessing;

/// <summary>Plans a download's tags: a target for every file, or a refusal that tags none.</summary>
// Lidarr groups a download by album tag, so a half-tagged folder imports as two albums that each miss tracks.
public static class WholeDownloadTags
{
    private static readonly CloseAlbumMatchSpecification LidarrCloseMatch = new(LogManager.CreateNullLogger());

    public sealed record Target(LocalTrack File, Track Track, AlbumRelease? Release);

    /// <param name="WrongRecordingFile">Set when the plan was refused on the fingerprint's word about this file.</param>
    public sealed record Plan(IReadOnlyList<Target> Targets, string? Refusal, string? WrongRecordingFile = null)
    {
        public bool WrongRecording => WrongRecordingFile != null;

        public static Plan Refuse(string reason) => new([], reason);
    }

    /// <summary>A target for every file across the identified releases, or why none may be written.</summary>
    public static Plan FromReleases(IReadOnlyList<LocalAlbumRelease> releases, int fileCount, double maxDistance, bool preferDigitalMedia, Func<LocalTrack, Track, bool> differentRecording)
    {
        // Before sleezer's distance gates, on every release Lidarr's own looser rule would import.
        foreach (LocalAlbumRelease release in releases.Where(LidarrWouldImport))
        {
            foreach ((LocalTrack file, Tuple<Track, Distance> match) in release.TrackMapping.Mapping)
            {
                if (differentRecording(file, match.Item1))
                    return WrongRecording(file, match.Item1);
            }
        }

        List<Target> targets = [];
        foreach (LocalAlbumRelease release in releases)
        {
            if (Refusal(release, maxDistance, preferDigitalMedia) is { } refusal)
                return Plan.Refuse(refusal);

            targets.AddRange(release.TrackMapping.Mapping.Select(m => new Target(m.Key, m.Value.Item1, release.AlbumRelease)));
        }

        return Whole(targets, fileCount, "map to a release track");
    }

    /// <summary>A target for every file the title fallback matched to a wanted track, or why none may be written.</summary>
    public static Plan FromTitles(IReadOnlyList<LocalTrack> files, IReadOnlyDictionary<int, int> mapping, IReadOnlyList<Track> tracks, AlbumRelease release, Func<LocalTrack, Track, bool> differentRecording)
    {
        List<Target> targets = [];
        foreach ((int fileIndex, int trackIndex) in mapping)
        {
            if (differentRecording(files[fileIndex], tracks[trackIndex]))
                return WrongRecording(files[fileIndex], tracks[trackIndex]);

            targets.Add(new Target(files[fileIndex], tracks[trackIndex], release));
        }

        return Whole(targets, files.Count, $"match a track title of '{release.Title}'");
    }

    private static bool LidarrWouldImport(LocalAlbumRelease release) =>
        release.TrackMapping != null && release.Distance != null && LidarrCloseMatch.IsSatisfiedBy(release, null).Accepted;

    private static Plan WrongRecording(LocalTrack file, Track track) => new([], DifferentRecording(file, track), file.Path);

    private static Plan Whole(List<Target> targets, int fileCount, string matched)
    {
        if (targets.Count != fileCount)
            return Plan.Refuse($"{targets.Count} of {fileCount} file(s) {matched}");

        // Two releases' tags read as two albums to Lidarr.
        int releases = targets.Select(t => t.Release?.ForeignReleaseId ?? (object?)t.Release).Distinct().Count();
        return releases > 1 ? Plan.Refuse($"the files map to {releases} releases") : new Plan(targets, null);
    }

    private static string? Refusal(LocalAlbumRelease release, double maxDistance, bool preferDigitalMedia)
    {
        double distance = release.Distance?.NormalizedDistance() ?? 1.0;
        if (distance > maxDistance || release.TrackMapping == null)
            return $"album match distance {distance:F3} exceeds max {maxDistance:F3}";

        // A storefront download is complete, so a length mismatch is the wrong edition: its album_id would penalise every other release.
        if (preferDigitalMedia && release.AlbumRelease is { } chosen && !DigitalReleaseSelector.FitsDownload(chosen, release.LocalTracks.Count))
            return $"'{chosen.Title}' has {chosen.TrackCount} track(s) but the download has {release.LocalTracks.Count}, a different edition";

        int weak = TrackMatchGate.WeakTracks(release.TrackMapping, maxDistance);
        return weak > 0 ? $"{weak}/{release.TrackMapping.Mapping.Count} track(s) of '{release.AlbumRelease?.Title ?? "<unknown>"}' match too weakly" : null;
    }

    private static string DifferentRecording(LocalTrack file, Track track) =>
        $"fingerprint says {Path.GetFileName(file.Path)} is a different recording than '{track.Title}'";
}
