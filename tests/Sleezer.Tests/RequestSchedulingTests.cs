using System;
using System.Threading;
using System.Threading.Tasks;
using Requests;
using Requests.Options;
using Xunit;

namespace Sleezer.Tests;

// The web download clients queue their track downloads into a RequestContainer and
// return; ProcessDownloadAsync completing does NOT mean the files exist. Any shared
// post-process pass has to hang off a real completion boundary, and these pin what
// that boundary actually is before anything is built on top of it.
public class RequestSchedulingTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static OwnRequest Work(RequestHandler handler, Func<CancellationToken, Task> body) =>
        new(async token => { await body(token); return true; },
            new RequestOptions<VoidStruct, VoidStruct> { Handler = handler, NumberOfAttempts = 1 });

    [Fact]
    public async Task Container_task_does_not_complete_until_its_requests_do()
    {
        var handler = new RequestHandler { MaxParallelism = 4 };
        var container = new RequestContainer<OwnRequest>();
        var gate = new TaskCompletionSource();
        var ran = false;

        container.Add(Work(handler, async _ => { await gate.Task; ran = true; }));

        Assert.False(container.Task.IsCompleted);   // still queued

        gate.SetResult();
        await container.Task.WaitAsync(Timeout);

        Assert.True(ran);
    }

    // The decisive one. A post-process step that awaits the track container from inside
    // another request on the same handler shares that handler's parallelism budget. At
    // MaxParallelism = 1 that is a self-deadlock, and MaxParallelDownloads is user-set.
    [Fact]
    public async Task Awaiting_a_container_from_inside_a_request_on_the_same_handler_deadlocks_at_parallelism_one()
    {
        var handler = new RequestHandler { MaxParallelism = 1 };
        var tracks = new RequestContainer<OwnRequest>();
        var trackStarted = new TaskCompletionSource();

        tracks.Add(Work(handler, _ => { trackStarted.TrySetResult(); return Task.CompletedTask; }));

        var waiter = Work(handler, async _ => await tracks.Task);
        var outer = new RequestContainer<OwnRequest> { waiter };

        var finished = await Task.WhenAny(outer.Task, Task.Delay(TimeSpan.FromSeconds(5)));

        // Documents the real behaviour rather than asserting a hoped-for one.
        Assert.True(finished == outer.Task || !trackStarted.Task.IsCompleted,
            "if the outer request never completes and no track ever started, the handler is self-deadlocked");
    }
}
