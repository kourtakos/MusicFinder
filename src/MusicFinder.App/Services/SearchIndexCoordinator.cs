using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using MusicFinder.Application.Search;
using MusicFinder.Domain.Contracts;
using MusicFinder.Infrastructure.Search;

namespace MusicFinder.App.Services;

/// <summary>
/// Keeps the smart-Greek-search index current: builds it at start-up (after an upgrade that is every track, in the background), every few
/// seconds while a scan is adding tracks, when a scan finishes, and after tags were edited. Searching works during all of that: tracks that
/// are not indexed yet are found by the original index, so the app never goes dark while this runs.
/// </summary>
public sealed class SearchIndexCoordinator : IDisposable
{
    private static readonly TimeSpan ScanPollInterval = TimeSpan.FromSeconds(3);

    private readonly SearchAliasIndexJob _job;
    private readonly FtsVocabulary _vocabulary;
    private readonly ScanCoordinator _scans;
    private readonly ILogger<SearchIndexCoordinator> _log;
    private readonly DispatcherTimer _scanTimer;
    private readonly SynchronizationContext _ui;
    private bool _started;

    /// <summary>"Preparing Greek search… 1,200 left", or empty when nothing is being prepared. Raised on the UI thread.</summary>
    public string StatusText { get; private set; } = "";
    public bool IsIndexing { get; private set; }
    public event EventHandler? StatusChanged;

    /// <summary>A run indexed something (so a search on screen may now find more). Raised on the UI thread.</summary>
    public event EventHandler? Indexed;

    public SearchIndexCoordinator(SearchAliasIndexJob job, FtsVocabulary vocabulary, ScanCoordinator scans, ITagEditingService tagEditing,
        ILogger<SearchIndexCoordinator> log)
    {
        _job = job;
        _vocabulary = vocabulary;
        _scans = scans;
        _log = log;
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();

        _scanTimer = new DispatcherTimer { Interval = ScanPollInterval };
        _scanTimer.Tick += (_, _) => { if (_scans.IsScanning) Run(); };
        _scans.StateChanged += (_, _) => _scanTimer.IsEnabled = _scans.IsScanning;
        _scans.ScanFinished += (_, _) => Run();
        tagEditing.TracksChanged += (_, _) => Run();

        _job.Progress += (_, p) => Post(() => SetStatus(p.Remaining > 0 ? $"Preparing Greek search… {p.Remaining:N0} left" : "", p.Remaining > 0));
    }

    /// <summary>Starts the first run. Call once on the UI thread after start-up.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        Run();
    }

    /// <summary>Indexes whatever is stale, in the background. Safe to call at any time: concurrent calls share one run.</summary>
    public void Run() => _ = RunAsync();

    public async Task RunAsync()
    {
        try
        {
            var count = await Task.Run(() => _job.RunAsync());
            await _vocabulary.EnsureLoadedAsync();
            Post(() => SetStatus("", false));
            if (count > 0) Post(() => Indexed?.Invoke(this, EventArgs.Empty));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Search index update failed");
            Post(() => SetStatus("", false));
        }
    }

    private void SetStatus(string text, bool indexing)
    {
        StatusText = text;
        IsIndexing = indexing;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Post(Action action)
    {
        if (SynchronizationContext.Current == _ui) action();
        else _ui.Post(_ => action(), null);
    }

    public void Dispose() => _scanTimer.Stop();
}
