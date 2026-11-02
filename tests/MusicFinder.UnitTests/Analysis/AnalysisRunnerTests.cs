using MusicFinder.Application.Analysis;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.UnitTests.Analysis;

public class AnalysisRunnerTests
{
    private sealed class FakeService : IAudioAnalysisService
    {
        public List<long> Seen { get; } = [];
        public int Concurrent;
        public int MaxConcurrent;
        public List<(ThreadPriority Priority, bool Pool)> Threads { get; } = [];
        public TimeSpan Work { get; set; } = TimeSpan.FromMilliseconds(30);
        public Func<long, bool>? Throws { get; set; }

        public async Task<AnalysisResult> AnalyzeAsync(long trackId, bool force, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref Concurrent);
            lock (this) { Seen.Add(trackId); MaxConcurrent = Math.Max(MaxConcurrent, now); Threads.Add((Thread.CurrentThread.Priority, Thread.CurrentThread.IsThreadPoolThread)); }
            try
            {
                if (Throws?.Invoke(trackId) == true) throw new InvalidOperationException("boom");
                Thread.Sleep(Work);                                       // a blocking wait: this is what a CPU-bound analysis looks like
                ct.ThrowIfCancellationRequested();
                await Task.CompletedTask;
                return new AnalysisResult(trackId, AnalysisOutcome.Analyzed, null, null);
            }
            finally { Interlocked.Decrement(ref Concurrent); }
        }

        public Task<TrackAnalysis?> GetRememberedAsync(long trackId, CancellationToken ct) => Task.FromResult<TrackAnalysis?>(null);
        public Task<IReadOnlyList<long>> FindTracksNeedingAnalysisAsync(AnalysisScope scope, CancellationToken ct) => Task.FromResult<IReadOnlyList<long>>([]);
        public Task RebindAfterWriteAsync(long trackId, long newSize, DateTime newModifiedUtc, CancellationToken ct) => Task.CompletedTask;
        public Task<int> CountAsync(CancellationToken ct) => Task.FromResult(0);
        public Task ClearAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Collector : IProgress<AnalysisProgress>
    {
        public List<AnalysisProgress> Items { get; } = [];
        public void Report(AnalysisProgress value) { lock (Items) Items.Add(value); }
    }

    private static List<long> Ids(int n) => Enumerable.Range(1, n).Select(i => (long)i).ToList();

    [Fact]
    public async Task EveryTrack_IsAnalysed_OnceEach_AndProgressCountsUp()
    {
        var service = new FakeService();
        var progress = new Collector();

        await new AnalysisRunner(service).RunAsync(Ids(8), false, 1, progress, default);

        Assert.Equal(Ids(8), service.Seen);
        Assert.Equal(Enumerable.Range(1, 8), progress.Items.Select(p => p.Done));
        Assert.All(progress.Items, p => Assert.Equal(8, p.Total));
    }

    [Fact]
    public async Task OneWorker_RunsOneTrackAtATime_TwoWorkersRunTwo()
    {
        var one = new FakeService();
        await new AnalysisRunner(one).RunAsync(Ids(6), false, 1, null, default);
        var two = new FakeService();
        await new AnalysisRunner(two).RunAsync(Ids(6), false, 2, null, default);

        Assert.Equal(1, one.MaxConcurrent);
        Assert.Equal(2, two.MaxConcurrent);
        Assert.Equal(Ids(6), two.Seen.Order());
    }

    [Fact]
    public async Task MoreThanTwoWorkers_IsAskedFor_ButOnlyTwoRun()
    {
        var service = new FakeService();

        await new AnalysisRunner(service).RunAsync(Ids(8), false, 9, null, default);

        Assert.Equal(2, service.MaxConcurrent);
    }

    [Fact]
    public async Task TheWork_RunsOnDedicatedBelowNormalPriorityThreads_NotTheThreadPool()
    {
        var service = new FakeService();

        await new AnalysisRunner(service).RunAsync(Ids(4), false, 2, null, default);

        Assert.All(service.Threads, t =>
        {
            Assert.Equal(ThreadPriority.BelowNormal, t.Priority);
            Assert.False(t.Pool);
        });
    }

    [Fact]
    public async Task Cancelling_StopsTheRun_BeforeTheRemainingTracks()
    {
        var service = new FakeService { Work = TimeSpan.FromMilliseconds(60) };
        using var cts = new CancellationTokenSource();
        var progress = new Collector();

        var run = new AnalysisRunner(service).RunAsync(Ids(50), false, 1, progress, cts.Token);
        await Task.Delay(200);
        cts.Cancel();
        await run;

        Assert.InRange(service.Seen.Count, 1, 15);
        Assert.True(progress.Items.Count < 50);
    }

    [Fact]
    public async Task AlreadyCancelled_DoesNothing()
    {
        var service = new FakeService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await new AnalysisRunner(service).RunAsync(Ids(5), false, 1, null, cts.Token);

        Assert.Empty(service.Seen);
    }

    [Fact]
    public async Task ATrackThatThrows_IsAnErrorResult_AndTheRestGoOn()
    {
        var service = new FakeService { Throws = id => id == 2 };
        var progress = new Collector();

        await new AnalysisRunner(service).RunAsync(Ids(4), false, 1, progress, default);

        Assert.Equal(4, progress.Items.Count);
        var failed = progress.Items.Single(p => p.TrackId == 2);
        Assert.Equal(AnalysisOutcome.Error, failed.Result.Outcome);
        Assert.Contains("boom", failed.Result.Message);
        Assert.Equal(3, progress.Items.Count(p => p.Result.Outcome == AnalysisOutcome.Analyzed));
    }

    [Fact]
    public async Task NoTracks_CompletesAtOnce()
    {
        var finished = new AnalysisRunner(new FakeService()).RunAsync([], false, 1, null, default);

        Assert.Same(finished, await Task.WhenAny(finished, Task.Delay(2000)));
    }

    [Fact]
    public async Task TheForceFlag_IsPassedOn()
    {
        var seen = new List<bool>();
        var service = new FlagService(seen);

        await new AnalysisRunner(service).RunAsync(Ids(2), true, 1, null, default);

        Assert.Equal([true, true], seen);
    }

    private sealed class FlagService(List<bool> seen) : IAudioAnalysisService
    {
        public Task<AnalysisResult> AnalyzeAsync(long trackId, bool force, CancellationToken ct)
        {
            lock (seen) seen.Add(force);
            return Task.FromResult(new AnalysisResult(trackId, AnalysisOutcome.Analyzed, null, null));
        }
        public Task<TrackAnalysis?> GetRememberedAsync(long trackId, CancellationToken ct) => Task.FromResult<TrackAnalysis?>(null);
        public Task<IReadOnlyList<long>> FindTracksNeedingAnalysisAsync(AnalysisScope scope, CancellationToken ct) => Task.FromResult<IReadOnlyList<long>>([]);
        public Task RebindAfterWriteAsync(long trackId, long newSize, DateTime newModifiedUtc, CancellationToken ct) => Task.CompletedTask;
        public Task<int> CountAsync(CancellationToken ct) => Task.FromResult(0);
        public Task ClearAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
