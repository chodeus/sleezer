using System.Text.Json;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek;
using NzbDrone.Plugin.Sleezer.Download.Clients.Soulseek.Models;
using Xunit;

namespace Sleezer.Tests;

// slskd reports transfers per peer directory, and two Lidarr albums can share
// one (the single/EP pluck takes a track out of a compilation folder). Handing
// the whole group to whoever owns file[0] starved the other item until it timed
// out and blocklisted a healthy release.
public class SlskdDirectoryPartitionerTests
{
    private const string Dir = @"@@peer\Artist\Album";
    private const string Peer = "peer";

    private static SlskdDownloadItem Item(params string[] names)
    {
        string source = "[" + string.Join(",", names.Select(n =>
            $"{{\"Filename\":{JsonSerializer.Serialize(Dir + "\\" + n)},\"Size\":1000}}")) + "]";
        return new SlskdDownloadItem(new ReleaseInfo { Source = source, Title = "t", DownloadUrl = "u" });
    }

    private static SlskdDownloadFile Transfer(string name) => new(
        Id: name, Username: Peer, Direction: "Download", Filename: Dir + "\\" + name,
        Size: 1000, StartOffset: 0, State: "InProgress",
        RequestedAt: DateTime.UtcNow, EnqueuedAt: DateTime.UtcNow, StartedAt: DateTime.UtcNow,
        BytesTransferred: 0, AverageSpeed: 0, BytesRemaining: 0,
        ElapsedTime: TimeSpan.Zero, PercentComplete: 0, RemainingTime: TimeSpan.Zero, EndedAt: null);

    private static SlskdDownloadDirectory Group(params string[] names) =>
        new(Dir, names.Length, names.Select(Transfer).ToList());

    private static string[] Names(SlskdDownloadDirectory? dir) =>
        dir?.Files?.Select(f => Path.GetFileName(f.Filename.Replace('\\', '/'))).ToArray() ?? [];

    private static SlskdDownloadDirectory SliceFor(SlskdDirectoryPartition partition, SlskdDownloadItem item) =>
        partition.Owners.Single(o => ReferenceEquals(o.Item, item)).Slice;

    [Fact]
    public void A_sole_owner_takes_the_whole_directory()
    {
        SlskdDownloadItem item = Item("01.flac", "02.flac");

        SlskdDirectoryPartition partition = SlskdDirectoryPartitioner.Partition(Group("01.flac", "02.flac"), [item], Peer);

        (SlskdDownloadItem owner, SlskdDownloadDirectory slice) = Assert.Single(partition.Owners);
        Assert.Same(item, owner);
        Assert.Equal(new[] { "01.flac", "02.flac" }, Names(slice));
        Assert.Equal(2, slice.FileCount);
        Assert.Null(partition.Unclaimed);
    }

    // The anti-starvation lock: before the split, one of these two got nothing.
    [Fact]
    public void Two_items_sharing_a_directory_each_get_their_own_files()
    {
        SlskdDownloadItem album = Item("01.flac", "02.flac");
        SlskdDownloadItem single = Item("03.flac");
        SlskdDownloadDirectory group = Group("01.flac", "02.flac", "03.flac");

        SlskdDirectoryPartition partition = SlskdDirectoryPartitioner.Partition(group, [album, single], Peer);

        Assert.Equal(2, partition.Owners.Count);
        Assert.Equal(new[] { "01.flac", "02.flac" }, Names(SliceFor(partition, album)));
        Assert.Equal(new[] { "03.flac" }, Names(SliceFor(partition, single)));
        Assert.Null(partition.Unclaimed);
        Assert.Equal(3, group.Files!.Count);
    }

    [Fact]
    public void The_owner_of_the_first_file_does_not_take_the_rest()
    {
        SlskdDownloadItem album = Item("02.flac", "03.flac");
        SlskdDownloadItem single = Item("01.flac");

        SlskdDirectoryPartition partition = SlskdDirectoryPartitioner.Partition(
            Group("01.flac", "02.flac", "03.flac"), [album, single], Peer);

        Assert.Equal(new[] { "02.flac", "03.flac" }, Names(SliceFor(partition, album)));
        Assert.Equal(new[] { "01.flac" }, Names(SliceFor(partition, single)));
        Assert.Null(partition.Unclaimed);
    }

    [Fact]
    public void An_enqueue_rejected_file_is_not_this_items_transfer()
    {
        SlskdDownloadItem item = Item("01.flac", "02.flac");
        item.MarkEnqueueFailed([Dir + @"\02.flac"]);

        SlskdDirectoryPartition partition = SlskdDirectoryPartitioner.Partition(Group("01.flac", "02.flac"), [item], Peer);

        Assert.Equal(new[] { "01.flac" }, Names(Assert.Single(partition.Owners).Slice));
        Assert.Equal(new[] { "02.flac" }, Names(partition.Unclaimed));
    }

    [Fact]
    public void Files_no_tracked_item_enqueued_are_unclaimed()
    {
        SlskdDownloadItem item = Item("01.flac");

        SlskdDirectoryPartition partition = SlskdDirectoryPartitioner.Partition(
            Group("foreign.flac", "stranger.flac"), [item], Peer);

        Assert.Empty(partition.Owners);
        Assert.Equal(new[] { "foreign.flac", "stranger.flac" }, Names(partition.Unclaimed));
    }

    [Fact]
    public void A_candidate_bound_to_another_peer_is_not_an_owner()
    {
        SlskdDownloadItem elsewhere = Item("01.flac");
        elsewhere.Username = "other-peer";
        SlskdDownloadItem unbound = Item("02.flac");

        SlskdDirectoryPartition partition = SlskdDirectoryPartitioner.Partition(
            Group("01.flac", "02.flac"), [elsewhere, unbound], Peer);

        (SlskdDownloadItem owner, SlskdDownloadDirectory slice) = Assert.Single(partition.Owners);
        Assert.Same(unbound, owner);
        Assert.Equal(new[] { "02.flac" }, Names(slice));
        Assert.Equal(new[] { "01.flac" }, Names(partition.Unclaimed));
    }

    [Fact]
    public void One_transfer_serves_both_items_that_enqueued_the_same_file()
    {
        SlskdDownloadItem album = Item("01.flac", "02.flac");
        SlskdDownloadItem single = Item("01.flac");

        SlskdDirectoryPartition partition = SlskdDirectoryPartitioner.Partition(
            Group("01.flac", "02.flac"), [album, single], Peer);

        Assert.Equal(new[] { "01.flac", "02.flac" }, Names(SliceFor(partition, album)));
        Assert.Equal(new[] { "01.flac" }, Names(SliceFor(partition, single)));
        Assert.Null(partition.Unclaimed);
    }

    [Fact]
    public void A_directory_with_no_files_yields_nothing()
    {
        SlskdDownloadItem item = Item("01.flac");

        SlskdDirectoryPartition empty = SlskdDirectoryPartitioner.Partition(new SlskdDownloadDirectory(Dir, 0, []), [item], Peer);
        SlskdDirectoryPartition missing = SlskdDirectoryPartitioner.Partition(new SlskdDownloadDirectory(Dir, 0, null), [item], Peer);

        Assert.Empty(empty.Owners);
        Assert.Null(empty.Unclaimed);
        Assert.Empty(missing.Owners);
        Assert.Null(missing.Unclaimed);
    }
}
