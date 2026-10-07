using System.Text.Json;
using NzbDrone.Plugin.Sleezer.Core.Model;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using NzbDrone.Plugin.Sleezer.Notifications.PlaylistExport;
using Xunit;

namespace Sleezer.Tests;

public class PlaylistSnapshotsTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 3, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);
    private static readonly List<PlaylistItem> Items = [new("artist-mbid", null, "Artist", "Album")];

    [Fact]
    public void a_list_with_no_snapshot_is_fetched()
    {
        PlaylistSnapshot? snapshot = PlaylistSnapshots.Resolve(null, Day, Now, "List", () => Items);

        Assert.NotNull(snapshot);
        Assert.Equal(Items, snapshot.Items);
        Assert.Equal(Now, snapshot.FetchedAt);
    }

    [Fact]
    public void a_fresh_snapshot_is_used_without_fetching()
    {
        PlaylistSnapshot stored = new("List", Items, Now.AddHours(-1));

        Assert.Same(stored, PlaylistSnapshots.Resolve(stored, Day, Now, "List", () => throw new InvalidOperationException("fetched")));
    }

    [Fact]
    public void a_stale_snapshot_is_refetched()
    {
        PlaylistSnapshot stored = new("List", [], Now.AddDays(-2));

        Assert.Equal(Items, PlaylistSnapshots.Resolve(stored, Day, Now, "List", () => Items)!.Items);
    }

    [Fact]
    public void an_empty_or_failed_fetch_keeps_the_stored_items()
    {
        PlaylistSnapshot stored = new("List", Items, Now.AddDays(-2));

        Assert.Same(stored, PlaylistSnapshots.Resolve(stored, Day, Now, "List", () => []));
        Assert.Same(stored, PlaylistSnapshots.Resolve(stored, Day, Now, "List", () => null));
    }
}

public sealed class PlaylistFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("playlist-file-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void playlist_starts_with_the_header_not_a_byte_order_mark()
    {
        string track = Path.Combine(_dir, "01 Track.flac");
        File.WriteAllText(track, "");
        string playlist = Path.Combine(_dir, "List.m3u8");

        Assert.Equal(1, PlaylistFile.Write(playlist, "List", [track], _dir, useRelative: true));

        Assert.Equal("#EXTM3U"u8.ToArray(), File.ReadAllBytes(playlist)[..7]);
        Assert.Contains("01 Track.flac", File.ReadAllLines(playlist));
    }

    [Fact]
    public void no_file_is_written_when_no_track_is_on_disk()
    {
        string playlist = Path.Combine(_dir, "List.m3u8");

        Assert.Equal(0, PlaylistFile.Write(playlist, "List", [Path.Combine(_dir, "missing.flac")], _dir, useRelative: false));
        Assert.False(File.Exists(playlist));
    }
}

public class FieldValueTests
{
    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("\"true\"", true)]
    [InlineData("null", false)]
    public void api_json_values_convert_without_throwing(string json, bool expected)
    {
        Assert.Equal(expected, FieldValue.ToBool(JsonDocument.Parse(json).RootElement.Clone()));
    }

    [Fact]
    public void plain_values_convert()
    {
        Assert.True(FieldValue.ToBool(true));
        Assert.True(FieldValue.ToBool("True"));
        Assert.False(FieldValue.ToBool(null));
    }
}
