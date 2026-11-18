using System;
using System.IO;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using Xunit;

namespace Sleezer.Tests;

// Lidarr 4.0 dropped AudioTag.MusicBrainzReleaseTrackId, so Bandcamp retagging named a member the host no longer had.
public class MusicBrainzTrackIdsTests : IDisposable
{
    private const string Recording = "2f3e1c0a-6a54-4c3b-9d1e-0c8f2b7a9e41";
    private const string ReleaseTrack = "9b7d4e2c-1f8a-4b6e-a3c5-7d2e9f0b1c63";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sleezer-mbids-" + Path.GetRandomFileName());

    public MusicBrainzTrackIdsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    // Same property shape as Lidarr 4.0's AudioTag; files written through the real one were checked outside this suite.
    private sealed class RecordingIdAudioTag : AudioTag
    {
        public string? MusicBrainzRecordingId { get; set; }
    }

    private sealed class UnknownTag
    {
        public string? MusicBrainzTrackId { get; set; } = ReleaseTrack;
    }

    private sealed class GuidRecordingTag
    {
        public string? MusicBrainzTrackId { get; set; } = ReleaseTrack;

        public Guid MusicBrainzRecordingId { get; set; }
    }

    [Fact]
    public void Before_lidarr_4_the_track_id_holds_the_recording()
    {
        var tag = new AudioTag();

        MusicBrainzTrackIds.Set(tag, Recording, ReleaseTrack);

        Assert.Equal(Recording, tag.MusicBrainzTrackId);
        Assert.Equal(ReleaseTrack, tag.MusicBrainzReleaseTrackId);
    }

    [Fact]
    public void With_a_recording_id_property_the_track_id_holds_the_release_track()
    {
        var tag = new RecordingIdAudioTag();

        MusicBrainzTrackIds.Set(tag, Recording, ReleaseTrack);

        Assert.Equal(ReleaseTrack, tag.MusicBrainzTrackId);
        Assert.Equal(Recording, tag.MusicBrainzRecordingId);
        Assert.Null(tag.MusicBrainzReleaseTrackId);
    }

    [Fact]
    public void Missing_ids_keep_the_tag_values_before_lidarr_4()
    {
        var tag = new AudioTag { MusicBrainzTrackId = Recording, MusicBrainzReleaseTrackId = ReleaseTrack };

        MusicBrainzTrackIds.Set(tag, null, null);

        Assert.Equal(Recording, tag.MusicBrainzTrackId);
        Assert.Equal(ReleaseTrack, tag.MusicBrainzReleaseTrackId);
    }

    [Fact]
    public void Missing_ids_keep_the_tag_values_with_a_recording_id_property()
    {
        var tag = new RecordingIdAudioTag { MusicBrainzTrackId = ReleaseTrack, MusicBrainzRecordingId = Recording };

        MusicBrainzTrackIds.Set(tag, null, null);

        Assert.Equal(ReleaseTrack, tag.MusicBrainzTrackId);
        Assert.Equal(Recording, tag.MusicBrainzRecordingId);
    }

    [Fact]
    public void An_unknown_tag_shape_is_left_alone_without_throwing()
    {
        var tag = new UnknownTag();

        MusicBrainzTrackIds.SetOn(tag, Recording, null);

        Assert.Equal(ReleaseTrack, tag.MusicBrainzTrackId);
    }

    [Fact]
    public void A_recording_id_property_of_another_type_is_left_alone_without_throwing()
    {
        var tag = new GuidRecordingTag();

        MusicBrainzTrackIds.SetOn(tag, Recording, null);

        Assert.Equal(ReleaseTrack, tag.MusicBrainzTrackId);
        Assert.Equal(Guid.Empty, tag.MusicBrainzRecordingId);
    }

    [Fact]
    public void Before_lidarr_4_the_ids_written_to_the_file_read_back()
    {
        string path = Path.Combine(_dir, "track.flac");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "silence.flac"), path);
        var tag = new AudioTag(path);

        MusicBrainzTrackIds.Set(tag, Recording, ReleaseTrack);
        tag.Write(path);

        var read = new AudioTag(path);
        Assert.Equal(Recording, read.MusicBrainzTrackId);
        Assert.Equal(ReleaseTrack, read.MusicBrainzReleaseTrackId);
    }
}
