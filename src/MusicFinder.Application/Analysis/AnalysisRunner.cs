using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.Application.Analysis;

/// <summary>One track finished (or failed): what came out, and how far along the run is.</summary>
public sealed record AnalysisProgress(long TrackId, AnalysisResult Result, int Done, int Total);

/// <summary>
/// Analyses many tracks in the background without getting in the way. The work runs on dedicated threads at below-normal priority (not on
/// the shared thread pool, whose threads cannot be de-prioritised), one or two at a time, so the interface and the search stay responsive
/// while hundreds of files are decoded. Cancelling stops between tracks and interrupts the one in progress.
/// </summary>
public sealed class AnalysisRunner
{
    public const int MaxConcurrency = 2;

    private readonly IAudioAnalysisService _service;

    public AnalysisRunner(IAudioAnalysisService service) => _service = service;

    public Task RunAsync(IReadOnlyList<long> trackIds, bool force, int concurrency, IProgress<AnalysisProgress>? progress, CancellationToken ct)
    {
        var workers = Math.Clamp(concurrency, 1, MaxConcurrency);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var next = -1;
        var done = 0;
        var running = workers;

        void Work()
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var index = Interlocked.Increment(ref next);
                    if (index >= trackIds.Count) break;
                    var id = trackIds[index];

                    AnalysisResult result;
                    try { result = _service.AnalyzeAsync(id, force, ct).GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { result = AnalysisResult.Failed(id, AnalysisOutcome.Error, "The analysis failed: " + ex.Message); }

                    progress?.Report(new AnalysisProgress(id, result, Interlocked.Increment(ref done), trackIds.Count));
                }
            }
            finally
            {
                if (Interlocked.Decrement(ref running) == 0) completion.TrySetResult();
            }
        }

        for (var i = 0; i < workers; i++)
            new Thread(Work) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "MusicFinder analysis " + (i + 1) }.Start();
        if (trackIds.Count == 0) { /* the workers find nothing to do and finish at once */ }
        return completion.Task;
    }
}
