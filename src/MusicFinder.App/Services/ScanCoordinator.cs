using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.App.Services;

/// <summary>
/// Runs library scans one at a time in the background and raises events on the UI thread.
/// Must be constructed on the UI thread so progress is marshalled there.
/// </summary>
public sealed class ScanCoordinator : IDisposable
{
    private readonly IIndexer _indexer;
    private readonly ILibraryRootRepository _roots;
    private readonly ILogger<ScanCoordinator> _log;
    private readonly SynchronizationContext _ui;
    private readonly Channel<long> _queue = Channel.CreateUnbounded<long>();
    private readonly HashSet<long> _pending = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly Progress<ScanProgress> _progress;
    private readonly Task _worker;

    public bool IsScanning { get; private set; }
    public string? CurrentRootPath { get; private set; }
    public ScanProgress? Current { get; private set; }

    /// <summary>Raised on the UI thread when scanning starts, progresses or stops.</summary>
    public event EventHandler? StateChanged;
    /// <summary>Raised on the UI thread after a root finished scanning (successfully or not).</summary>
    public event EventHandler<long>? ScanFinished;

    public ScanCoordinator(IIndexer indexer, ILibraryRootRepository roots, ILogger<ScanCoordinator> log)
    {
        _indexer = indexer;
        _roots = roots;
        _log = log;
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        _progress = new Progress<ScanProgress>(p =>
        {
            Current = p;
            StateChanged?.Invoke(this, EventArgs.Empty);
        });
        _worker = Task.Run(RunAsync);
    }

    public void Enqueue(long rootId)
    {
        lock (_pending)
        {
            if (!_pending.Add(rootId)) return; // already queued or running
        }
        _queue.Writer.TryWrite(rootId);
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var rootId in _queue.Reader.ReadAllAsync(_cts.Token))
            {
                var root = await _roots.GetAsync(rootId, _cts.Token);
                if (root is null || !root.Enabled) { Release(rootId); continue; }

                _ui.Post(_ => { IsScanning = true; CurrentRootPath = root.Path; Current = null; StateChanged?.Invoke(this, EventArgs.Empty); }, null);
                try
                {
                    await _indexer.ScanRootAsync(rootId, _progress, _cts.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Scan of root {RootId} failed.", rootId);
                }
                Release(rootId);
                var finished = rootId;
                _ui.Post(_ =>
                {
                    bool more;
                    lock (_pending) more = _pending.Count > 0;
                    IsScanning = more;
                    if (!more) { CurrentRootPath = null; Current = null; }
                    StateChanged?.Invoke(this, EventArgs.Empty);
                    ScanFinished?.Invoke(this, finished);
                }, null);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Release(long rootId)
    {
        lock (_pending) _pending.Remove(rootId);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _queue.Writer.TryComplete();
        try { _worker.Wait(TimeSpan.FromSeconds(3)); } catch { /* shutting down */ }
        _cts.Dispose();
    }
}
