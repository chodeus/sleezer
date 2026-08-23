using System;
using System.Threading;
using System.Threading.Tasks;
using Requests;
using Requests.Options;
using Xunit;

namespace Sleezer.Tests;

// The web download clients queue their track downloads and return, so a shared
// post-process pass has to hang off a real completion boundary. Each of these pins one
// scheduler behaviour the boundary depends on — reasoning about them was wrong twice,
// and one wrong assumption shipped a deadlock.
public class RequestSchedulingTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(6);

    private static OwnRequest Work(RequestHandler h, Func<CancellationToken, Task> body) =>
        new(async t => { await body(t); return true; },
            new RequestOptions<VoidStruct, VoidStruct> { Handler = h, NumberOfAttempts = 1 });

    private static async Task<bool> Finishes(RequestContainer<OwnRequest> c) =>
        await Task.WhenAny(c.Task, Task.Delay(Settle)) == c.Task;

    [Fact]
    public async Task Container_task_does_not_complete_until_its_requests_do()
    {
        var handler = new RequestHandler { MaxParallelism = 4 };
        var container = new RequestContainer<OwnRequest>();
        var gate = new TaskCompletionSource();

        container.Add(Work(handler, async _ => await gate.Task));
        Assert.False(container.Task.IsCompleted);

        gate.SetResult();
        Assert.True(await Finishes(container));
    }

    // Why awaiting the track container alone is not enough: the clients chain their
    // per-track tagging with TrySetSubsequentRequest, and the container ignores it.
    [Fact]
    public async Task Container_task_excludes_requests_chained_onto_its_members()
    {
        var handler = new RequestHandler { MaxParallelism = 2 };
        var tracks = new RequestContainer<OwnRequest>();
        var gate = new TaskCompletionSource();
        var subsequentRan = false;

        var download = Work(handler, _ => Task.CompletedTask);
        var subsequent = Work(handler, async _ => { await gate.Task; subsequentRan = true; });
        download.TrySetSubsequentRequest(subsequent);
        tracks.Add(download);

        Assert.True(await Finishes(tracks));
        Assert.False(subsequentRan);      // the container finished without it

        gate.SetResult();
    }

    // The deadlock that shipped in v1.15.0. A parent that occupies a handler slot and
    // then awaits children needing that same slot never completes at parallelism 1.
    [Fact]
    public async Task Parent_sharing_the_download_handler_deadlocks_at_parallelism_one()
    {
        var shared = new RequestHandler { MaxParallelism = 1 };
        var tracks = new RequestContainer<OwnRequest>();
        var childRan = false;

        var parent = Work(shared, async _ =>
        {
            tracks.Add(Work(shared, __ => { childRan = true; return Task.CompletedTask; }));
            await tracks.Task;
        });

        Assert.False(await Finishes(new RequestContainer<OwnRequest> { parent }));
        Assert.False(childRan);
    }

    // And why BaseDownloadRequest.OrchestrationHandler exists: a separate handler frees
    // the download capacity the children need, at every parallelism setting.
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Parent_on_its_own_handler_completes_at_any_parallelism(int parallelism)
    {
        var downloads = new RequestHandler { MaxParallelism = parallelism };
        var orchestration = new RequestHandler { MaxParallelism = 16 };
        var tracks = new RequestContainer<OwnRequest>();
        var childRan = false;

        var parent = Work(orchestration, async _ =>
        {
            tracks.Add(Work(downloads, __ => { childRan = true; return Task.CompletedTask; }));
            await tracks.Task;
        });

        Assert.True(await Finishes(new RequestContainer<OwnRequest> { parent }));
        Assert.True(childRan);
    }
}
