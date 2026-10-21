using NLog;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Sleezer.Core.PostProcessing;
using Xunit;

namespace Sleezer.Tests;

public class FingerprintLookupTests
{
    private static readonly Logger Log = LogManager.CreateNullLogger();

    // Lidarr's Lookup sends all its files in one request. Past AcoustID's size limit the answer is
    // "request too large", which Lidarr logs at Debug, leaving every file without results.
    private sealed class FakeAcoustId(int limit = 20) : IFingerprintingService
    {
        public bool Setup { get; init; } = true;
        public Action<int>? OnBatch { get; init; }
        public Func<int, bool> Answers { get; init; } = _ => true;
        public List<string> Requested { get; } = [];
        public List<int> Batches { get; } = [];

        public bool IsSetup() => Setup;

        public Version FpcalcVersion() => new(1, 5, 1);

        public void Lookup(List<LocalTrack> tracks, double threshold)
        {
            lock (Batches)
            {
                Batches.Add(tracks.Count);
                Requested.AddRange(tracks.Select(t => t.Path));
            }

            int batch = Batches.Count - 1;
            OnBatch?.Invoke(batch);
            if (tracks.Count > limit || !Answers(batch))
                return;
            foreach (LocalTrack track in tracks)
                track.AcoustIdResults = [$"recording-of-{track.Path}"];
        }
    }

    private static List<LocalTrack> Files(int count) => [.. Enumerable.Range(1, count).Select(i => new LocalTrack { Path = $"/downloads/Moonward/{i:00} - Lantern Song.flac" })];

    [Fact]
    public async Task A_whole_release_gets_results_in_batches_AcoustID_accepts()
    {
        List<LocalTrack> tracks = Files(61);
        new FakeAcoustId().Lookup(tracks, 0.5);
        Assert.All(tracks, t => Assert.Null(t.AcoustIdResults));

        FakeAcoustId acoustId = new();
        await FingerprintLookup.RunAsync(acoustId, tracks, 0.5, Log, CancellationToken.None);

        Assert.All(tracks, t => Assert.Equal([$"recording-of-{t.Path}"], t.AcoustIdResults));
    }

    [Theory]
    [InlineData(1, new[] { 1 })]
    [InlineData(10, new[] { 10 })]
    [InlineData(11, new[] { 10, 1 })]
    [InlineData(61, new[] { 10, 10, 10, 10, 10, 10, 1 })]
    public async Task Each_file_is_looked_up_once_in_order(int count, int[] batches)
    {
        List<LocalTrack> tracks = Files(count);
        FakeAcoustId acoustId = new();

        await FingerprintLookup.RunAsync(acoustId, tracks, 0.5, Log, CancellationToken.None);

        Assert.Equal(batches, acoustId.Batches);
        Assert.Equal(tracks.Select(t => t.Path), acoustId.Requested);
    }

    [Fact]
    public async Task A_failed_batch_leaves_only_its_files_unverifiable()
    {
        List<LocalTrack> tracks = Files(25);
        FakeAcoustId acoustId = new() { OnBatch = i => { if (i == 1) throw new InvalidOperationException("fpcalc failed"); } };

        await FingerprintLookup.RunAsync(acoustId, tracks, 0.5, Log, CancellationToken.None);

        Assert.Equal([10, 10, 5], acoustId.Batches);
        Assert.All(tracks[..10].Concat(tracks[20..]), t => Assert.NotNull(t.AcoustIdResults));
        Assert.All(tracks[10..20], t => Assert.Null(t.AcoustIdResults));
    }

    // The 5-file row cancels in the last batch: nothing is left to start, yet the cancellation still surfaces.
    [Theory]
    [InlineData(25)]
    [InlineData(5)]
    public async Task A_cancelled_lookup_starts_no_further_batch(int count)
    {
        using CancellationTokenSource cts = new();
        FakeAcoustId acoustId = new() { OnBatch = _ => cts.Cancel() };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FingerprintLookup.RunAsync(acoustId, Files(count), 0.5, Log, cts.Token));
        await Task.Delay(200);

        Assert.Single(acoustId.Batches);
    }

    [Fact]
    public async Task An_already_cancelled_lookup_starts_none()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();
        FakeAcoustId acoustId = new();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FingerprintLookup.RunAsync(acoustId, Files(5), 0.5, Log, cts.Token));
        await Task.Delay(200);

        Assert.Empty(acoustId.Batches);
    }

    // Scaled down: a "timeout" here is a lookup slower than 100 ms, and the pause before asking again is 10 ms.
    private static Task RunScaled(FakeAcoustId acoustId, List<LocalTrack> tracks, CancellationToken ct = default, int pauseMs = 10) =>
        FingerprintLookup.RunAsync(acoustId, tracks, 0.5, Log, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(pauseMs), ct);

    [Fact]
    public async Task A_batch_that_timed_out_is_asked_once_more()
    {
        List<LocalTrack> tracks = Files(10);
        FakeAcoustId acoustId = new() { OnBatch = i => { if (i == 0) Thread.Sleep(150); }, Answers = i => i > 0 };

        await RunScaled(acoustId, tracks);

        Assert.Equal([10, 10], acoustId.Batches);
        Assert.All(tracks, t => Assert.NotNull(t.AcoustIdResults));
    }

    [Fact]
    public async Task A_batch_answered_empty_in_time_is_not_asked_again()
    {
        FakeAcoustId acoustId = new() { Answers = _ => false };

        await RunScaled(acoustId, Files(10));

        Assert.Equal([10], acoustId.Batches);
    }

    [Fact]
    public async Task A_slow_batch_with_answers_is_not_asked_again()
    {
        FakeAcoustId acoustId = new() { OnBatch = _ => Thread.Sleep(150) };

        await RunScaled(acoustId, Files(10));

        Assert.Equal([10], acoustId.Batches);
    }

    [Fact]
    public async Task A_batch_is_asked_again_only_once()
    {
        List<LocalTrack> tracks = Files(10);
        FakeAcoustId acoustId = new() { OnBatch = _ => Thread.Sleep(150), Answers = _ => false };

        await RunScaled(acoustId, tracks);

        Assert.Equal([10, 10], acoustId.Batches);
        Assert.All(tracks, t => Assert.Null(t.AcoustIdResults));
    }

    [Fact]
    public async Task Only_the_batch_that_timed_out_is_asked_again()
    {
        List<LocalTrack> tracks = Files(25);
        FakeAcoustId acoustId = new() { OnBatch = i => { if (i == 1) Thread.Sleep(150); }, Answers = i => i != 1 };

        await RunScaled(acoustId, tracks);

        Assert.Equal([10, 10, 10, 5], acoustId.Batches);
        Assert.Equal(tracks.Take(10).Concat(tracks.Skip(10).Take(10)).Concat(tracks.Skip(10).Take(10)).Concat(tracks.Skip(20)).Select(t => t.Path), acoustId.Requested);
        Assert.All(tracks, t => Assert.NotNull(t.AcoustIdResults));
    }

    [Fact]
    public async Task AcoustID_still_down_after_a_retry_gets_no_more_retries()
    {
        FakeAcoustId acoustId = new() { OnBatch = _ => Thread.Sleep(150), Answers = _ => false };

        await RunScaled(acoustId, Files(30));

        Assert.Equal([10, 10, 10, 10], acoustId.Batches);
    }

    [Fact]
    public async Task A_retry_that_got_answers_keeps_retries_for_later_batches()
    {
        List<LocalTrack> tracks = Files(20);
        FakeAcoustId acoustId = new() { OnBatch = i => { if (i % 2 == 0) Thread.Sleep(150); }, Answers = i => i % 2 == 1 };

        await RunScaled(acoustId, tracks);

        Assert.Equal([10, 10, 10, 10], acoustId.Batches);
        Assert.All(tracks, t => Assert.NotNull(t.AcoustIdResults));
    }

    [Fact]
    public async Task A_cancelled_pause_asks_no_more()
    {
        using CancellationTokenSource cts = new();
        FakeAcoustId acoustId = new() { OnBatch = _ => { Thread.Sleep(150); cts.CancelAfter(50); }, Answers = _ => false };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunScaled(acoustId, Files(10), cts.Token, pauseMs: 5000));

        Assert.Single(acoustId.Batches);
    }

    [Theory]
    [InlineData(false, 5)]
    [InlineData(true, 0)]
    public async Task No_fpcalc_or_no_files_makes_no_lookup(bool setup, int count)
    {
        FakeAcoustId acoustId = new() { Setup = setup };

        await FingerprintLookup.RunAsync(acoustId, Files(count), 0.5, Log, CancellationToken.None);

        Assert.Empty(acoustId.Batches);
    }
}
