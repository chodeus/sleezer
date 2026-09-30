using NzbDrone.Core.MediaFiles.TrackImport.Identification;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Core.PostProcessing;
using Xunit;

namespace Sleezer.Tests;

public class TrackMatchGateTests
{
    private static TrackMapping Mapping(params double[] trackDistances)
    {
        TrackMapping mapping = new();
        for (int i = 0; i < trackDistances.Length; i++)
        {
            Distance distance = new();
            distance.Add("track_title", trackDistances[i]);
            mapping.Mapping.Add(new LocalTrack { Path = $"/downloads/{i + 1:00}.flac" }, Tuple.Create(new Track(), distance));
        }

        return mapping;
    }

    [Fact]
    public void WeakTracks_counts_the_tracks_over_the_limit()
    {
        Assert.Equal(2, TrackMatchGate.WeakTracks(Mapping(0.0, 0.4, 0.3), 0.15));
    }

    [Fact]
    public void WeakTracks_is_zero_when_every_track_is_within_the_limit()
    {
        Assert.Equal(0, TrackMatchGate.WeakTracks(Mapping(0.0, 0.15, 0.1), 0.15));
    }

    [Fact]
    public void WeakTracks_counts_a_track_with_no_distance_as_weak()
    {
        TrackMapping mapping = new();
        mapping.Mapping.Add(new LocalTrack { Path = "/downloads/01.flac" }, Tuple.Create<Track, Distance>(new Track(), null!));

        Assert.Equal(1, TrackMatchGate.WeakTracks(mapping, 0.15));
    }
}
