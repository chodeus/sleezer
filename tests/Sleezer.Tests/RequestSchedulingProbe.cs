using System;
using System.Threading;
using System.Threading.Tasks;
using Requests;
using Requests.Options;
using Xunit;
using Xunit.Abstractions;

namespace Sleezer.Tests;

public class RequestSchedulingProbe(ITestOutputHelper output)
{
    private static OwnRequest Work(RequestHandler h, Func<CancellationToken, Task> body) =>
        new(async t => { await body(t); return true; },
            new RequestOptions<VoidStruct, VoidStruct> { Handler = h, NumberOfAttempts = 1 });

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Probe_awaiting_container_from_inside_request(int parallelism)
    {
        var handler = new RequestHandler { MaxParallelism = parallelism };
        var tracks = new RequestContainer<OwnRequest>();
        var trackRan = false;

        tracks.Add(Work(handler, _ => { trackRan = true; return Task.CompletedTask; }));

        var waiterDone = false;
        var waiter = Work(handler, async _ => { await tracks.Task; waiterDone = true; });
        var outer = new RequestContainer<OwnRequest> { waiter };

        var completed = await Task.WhenAny(outer.Task, Task.Delay(TimeSpan.FromSeconds(4))) == outer.Task;
        output.WriteLine($"PROBE parallelism={parallelism} outerCompleted={completed} trackRan={trackRan} waiterDone={waiterDone}");
        Assert.True(true);
    }

    // Decisive: the clients chain per-track tagging onto each download with
    // TrySetSubsequentRequest. If the container's Task already accounts for those,
    // awaiting the track container alone is a sufficient boundary for the shared pass.
    [Fact]
    public async Task Probe_does_container_task_cover_a_subsequent_request()
    {
        var handler = new RequestHandler { MaxParallelism = 2 };
        var tracks = new RequestContainer<OwnRequest>();

        var subsequentRan = false;
        var gate = new TaskCompletionSource();

        var download = Work(handler, _ => Task.CompletedTask);
        var subsequent = Work(handler, async _ => { await gate.Task; subsequentRan = true; });
        download.TrySetSubsequentRequest(subsequent);
        subsequent.TrySetIdle();
        tracks.Add(download);

        var earlyFinish = await Task.WhenAny(tracks.Task, Task.Delay(TimeSpan.FromSeconds(2))) == tracks.Task;
        output.WriteLine($"PROBE2 containerTaskDoneBeforeSubsequent={earlyFinish} subsequentRan={subsequentRan}");

        gate.SetResult();
        await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(2)));
        output.WriteLine($"PROBE2 afterRelease subsequentRan={subsequentRan} containerDone={tracks.Task.IsCompleted}");
        Assert.True(true);
    }

    // If idle requests count as done, a container of them is a useless boundary.
    [Fact]
    public async Task Probe_does_a_container_wait_for_idle_members()
    {
        var handler = new RequestHandler { MaxParallelism = 2 };
        var pending = new RequestContainer<OwnRequest>();

        var ran = false;
        var idle = Work(handler, _ => { ran = true; return Task.CompletedTask; });
        idle.TrySetIdle();
        pending.Add(idle);

        var doneWhileIdle = await Task.WhenAny(pending.Task, Task.Delay(TimeSpan.FromSeconds(2))) == pending.Task;
        output.WriteLine($"PROBE3 containerDoneWhileMemberIdle={doneWhileIdle} ran={ran}");
        Assert.True(true);
    }
}
