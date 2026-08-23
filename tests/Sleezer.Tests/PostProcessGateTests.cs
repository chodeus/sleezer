using System;
using System.Threading.Tasks;
using NzbDrone.Core.Download;
using NzbDrone.Plugin.Sleezer.Core.Download;
using Xunit;

namespace Sleezer.Tests;

// Observed live on the first Qobuz download: Lidarr imported the folder before the
// corruption scan ran. DoDownload marks the item Completed and the proxies surface
// exactly the Completed items, so the item must not read Completed while post-process
// is still working on the files.
public class PostProcessGateTests
{
    private sealed class Item : IQueuedDownload
    {
        public string ID { get; set; } = "1";
        public string Title { get; set; } = "An Album";
        public DownloadItemStatus Status { get; set; }
    }

    [Fact]
    public async Task Does_not_read_completed_while_post_process_is_running()
    {
        var item = new Item { Status = DownloadItemStatus.Completed };
        DownloadItemStatus observed = DownloadItemStatus.Completed;

        await PostProcessGate.RunHeldAsync(item, () =>
        {
            observed = item.Status;   // what Lidarr's poll would see mid-flight
            return Task.FromResult(true);
        });

        Assert.NotEqual(DownloadItemStatus.Completed, observed);
        Assert.Equal(DownloadItemStatus.Completed, item.Status);
    }

    [Fact]
    public async Task Marks_the_item_failed_when_post_process_rejects_it()
    {
        var item = new Item { Status = DownloadItemStatus.Completed };

        bool accepted = await PostProcessGate.RunHeldAsync(item, () => Task.FromResult(false));

        Assert.False(accepted);
        Assert.Equal(DownloadItemStatus.Failed, item.Status);
    }

    // Leaving it Downloading would strand it: never imported, never cleaned up.
    [Fact]
    public async Task Does_not_strand_the_item_when_post_process_throws()
    {
        var item = new Item { Status = DownloadItemStatus.Completed };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PostProcessGate.RunHeldAsync(item, () => throw new InvalidOperationException("scan blew up")));

        Assert.Equal(DownloadItemStatus.Failed, item.Status);
    }

    [Theory]
    [InlineData(DownloadItemStatus.Failed)]
    [InlineData(DownloadItemStatus.Warning)]
    public async Task Leaves_a_download_that_did_not_complete_alone(DownloadItemStatus status)
    {
        var item = new Item { Status = status };
        bool ran = false;

        bool accepted = await PostProcessGate.RunHeldAsync(item, () => { ran = true; return Task.FromResult(true); });

        Assert.True(accepted);
        Assert.False(ran);
        Assert.Equal(status, item.Status);
    }
}
