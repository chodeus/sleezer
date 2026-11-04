using NzbDrone.Core.MediaFiles.TrackImport.Identification;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Core.PostProcessing;
using Xunit;

namespace Sleezer.Tests;

// Live 2026-11-04: Fatboy Slim 3 of 4, Axwell 1 of 3 and Pryda 1 of 2 files tagged; Lidarr split each folder by album tag and refused both halves.
public class WholeDownloadTagsTests
{
    private const double Limit = 0.15;

    private static readonly Func<LocalTrack, Track, bool> SameRecording = (_, _) => false;

    private static LocalTrack File(int n) => new() { Path = $"/downloads/{n:00}.flac" };

    private static Distance Dist(double value)
    {
        Distance distance = new();
        distance.Add("track_title", value);
        return distance;
    }

    private static LocalAlbumRelease Release(string title, double albumDistance, IReadOnlyList<LocalTrack> files, int mapped, int trackCount, params double[] trackDistances)
    {
        TrackMapping mapping = new() { LocalExtra = [.. files.Skip(mapped)], MBExtra = [] };
        for (int i = 0; i < mapped; i++)
            mapping.Mapping.Add(files[i], Tuple.Create(new Track { Title = $"{title} {i + 1}", AbsoluteTrackNumber = i + 1 }, Dist(trackDistances.Length > i ? trackDistances[i] : 0.0)));

        return new LocalAlbumRelease([.. files])
        {
            Distance = Dist(albumDistance),
            TrackMapping = mapping,
            AlbumRelease = new AlbumRelease { Title = title, ForeignReleaseId = title, TrackCount = trackCount, Media = [new Medium { Number = 1, Format = "Digital Media" }] }
        };
    }

    [Fact]
    public void Plans_every_file_when_every_file_maps()
    {
        LocalTrack[] files = [File(1), File(2), File(3)];

        WholeDownloadTags.Plan plan = WholeDownloadTags.FromReleases([Release("Nobody Else", 0.05, files, 3, 3)], 3, Limit, false, SameRecording);

        Assert.Null(plan.Refusal);
        Assert.Equal(files.Select(f => f.Path), plan.Targets.Select(t => t.File.Path).Order());
    }

    // Axwell: the 1-track release took one of three files and left two unmatched.
    [Fact]
    public void Refuses_when_a_release_leaves_files_unmapped()
    {
        LocalTrack[] files = [File(1), File(2), File(3)];

        WholeDownloadTags.Plan plan = WholeDownloadTags.FromReleases([Release("Nobody Else", 0.05, files, 1, 1)], 3, Limit, false, SameRecording);

        Assert.Empty(plan.Targets);
        Assert.Equal("1 of 3 file(s) map to a release track", plan.Refusal);
    }

    // Fatboy Slim: three files matched the release, the fourth was a different recording.
    [Fact]
    public void Refuses_every_file_when_one_is_a_different_recording()
    {
        LocalTrack[] files = [File(1), File(2), File(3), File(4)];
        Func<LocalTrack, Track, bool> fourthDiffers = (file, _) => file.Path == files[3].Path;

        WholeDownloadTags.Plan plan = WholeDownloadTags.FromReleases([Release("The Beat Burger", 0.05, files, 4, 4)], 4, Limit, false, fourthDiffers);

        Assert.Empty(plan.Targets);
        Assert.Equal("fingerprint says 04.flac is a different recording than 'The Beat Burger 4'", plan.Refusal);
    }

    // Lidarr grouped the files by album tag into two releases; only one of them matched.
    [Fact]
    public void Refuses_every_file_when_another_release_misses_the_limit()
    {
        LocalAlbumRelease close = Release("LYCKA / F.A.T", 0.05, [File(1)], 1, 2);
        LocalAlbumRelease far = Release("Lycka", 0.40, [File(2)], 1, 1);

        WholeDownloadTags.Plan plan = WholeDownloadTags.FromReleases([close, far], 2, Limit, false, SameRecording);

        Assert.Empty(plan.Targets);
        Assert.Equal("album match distance 0.400 exceeds max 0.150", plan.Refusal);
    }

    // Each release's tags are an album of their own to Lidarr.
    [Fact]
    public void Refuses_when_the_files_map_to_two_releases()
    {
        LocalAlbumRelease lycka = Release("Lycka", 0.05, [File(1)], 1, 1);
        LocalAlbumRelease fat = Release("F.A.T", 0.05, [File(2)], 1, 1);

        WholeDownloadTags.Plan plan = WholeDownloadTags.FromReleases([lycka, fat], 2, Limit, false, SameRecording);

        Assert.Empty(plan.Targets);
        Assert.Equal("the files map to 2 releases", plan.Refusal);
    }

    [Fact]
    public void Refuses_when_a_track_matches_too_weakly()
    {
        LocalTrack[] files = [File(1), File(2)];

        WholeDownloadTags.Plan plan = WholeDownloadTags.FromReleases([Release("Jungle Jam / What You Do", 0.05, files, 2, 2, 0.0, 0.3)], 2, Limit, false, SameRecording);

        Assert.Empty(plan.Targets);
        Assert.Equal("1/2 track(s) of 'Jungle Jam / What You Do' match too weakly", plan.Refusal);
    }

    [Fact]
    public void Refuses_a_storefront_download_on_a_release_of_another_length()
    {
        LocalTrack[] files = [File(1), File(2)];

        WholeDownloadTags.Plan plan = WholeDownloadTags.FromReleases([Release("Heartbreak Anthem", 0.05, files, 2, 3)], 2, Limit, true, SameRecording);

        Assert.Empty(plan.Targets);
        Assert.Equal("'Heartbreak Anthem' has 3 track(s) but the download has 2, a different edition", plan.Refusal);
    }

    [Fact]
    public void Refuses_when_nothing_was_identified()
    {
        WholeDownloadTags.Plan plan = WholeDownloadTags.FromReleases([], 2, Limit, false, SameRecording);

        Assert.Equal("0 of 2 file(s) map to a release track", plan.Refusal);
    }

    [Fact]
    public void Title_fallback_plans_every_file_when_every_title_matches()
    {
        LocalTrack[] files = [File(1), File(2)];
        Track[] tracks = [new() { Title = "Jungle Jam" }, new() { Title = "What You Do" }];
        AlbumRelease release = new() { Title = "Jungle Jam / What You Do" };

        WholeDownloadTags.Plan plan = WholeDownloadTags.FromTitles(files, new Dictionary<int, int> { [0] = 1, [1] = 0 }, tracks, release, SameRecording);

        Assert.Null(plan.Refusal);
        Assert.Equal(["What You Do", "Jungle Jam"], plan.Targets.Select(t => t.Track.Title));
        Assert.All(plan.Targets, t => Assert.Same(release, t.Release));
    }

    // Pryda, Marnik, Redeyes: the title fallback tagged the one title it matched and left the other file.
    [Fact]
    public void Title_fallback_refuses_when_a_file_matches_no_title()
    {
        LocalTrack[] files = [File(1), File(2)];
        Track[] tracks = [new() { Title = "LYCKA" }, new() { Title = "(F. A.T)" }];

        WholeDownloadTags.Plan plan = WholeDownloadTags.FromTitles(files, new Dictionary<int, int> { [0] = 0 }, tracks, new AlbumRelease { Title = "LYCKA / F.A.T" }, SameRecording);

        Assert.Empty(plan.Targets);
        Assert.Equal("1 of 2 file(s) match a track title of 'LYCKA / F.A.T'", plan.Refusal);
    }

    [Fact]
    public void Title_fallback_refuses_every_file_when_one_is_a_different_recording()
    {
        LocalTrack[] files = [File(1), File(2)];
        Track[] tracks = [new() { Title = "Gam Gam" }, new() { Title = "Gam Gam (extended mix)" }];
        Func<LocalTrack, Track, bool> secondDiffers = (file, _) => file.Path == files[1].Path;

        WholeDownloadTags.Plan plan = WholeDownloadTags.FromTitles(files, new Dictionary<int, int> { [0] = 0, [1] = 1 }, tracks, new AlbumRelease { Title = "Gam Gam" }, secondDiffers);

        Assert.Empty(plan.Targets);
        Assert.Equal("fingerprint says 02.flac is a different recording than 'Gam Gam (extended mix)'", plan.Refusal);
    }
}
