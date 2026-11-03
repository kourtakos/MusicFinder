using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.App.Services;

/// <summary>
/// Reads the DJ programs' history without being asked: once at start-up, when a session starts or stops, and every minute while one is live.
/// Switched off by the "read automatically" setting. Reading is read-only and quiet; a failure is only logged here (the result shows in Settings).
/// </summary>
public sealed class HistoryAutoImporter : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private readonly IHistoryImportService _import;
    private readonly ISessionService _session;
    private readonly ISettingsStore _settings;
    private readonly ILogger _log;
    private readonly Timer _timer;
    private int _running;
    private bool _disposed;

    public HistoryAutoImporter(IHistoryImportService import, ISessionService session, ISettingsStore settings, ILogger<HistoryAutoImporter>? log = null)
    {
        _import = import;
        _session = session;
        _settings = settings;
        _log = log ?? NullLogger<HistoryAutoImporter>.Instance;
        _timer = new Timer(_ => Trigger(), null, Timeout.Infinite, Timeout.Infinite);
        _session.SessionChanged += (_, _) => OnSessionChanged();
    }

    /// <summary>Call once the library is up.</summary>
    public void Start()
    {
        Trigger();
        Rearm();
    }

    private void OnSessionChanged()
    {
        Trigger();
        Rearm();
    }

    private void Rearm()
    {
        if (_disposed) return;
        var live = _session.Active is not null;
        try { _timer.Change(live ? Interval : Timeout.InfiniteTimeSpan, live ? Interval : Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { /* shutting down */ }
    }

    private void Trigger()
    {
        if (_disposed || !_settings.Load().HistoryAutoImport) return;
        if (Interlocked.Exchange(ref _running, 1) == 1) return;          // a pass is already running; the next tick will catch up
        _ = Task.Run(async () =>
        {
            try { await _import.RunAsync(null); }
            catch (Exception ex) { _log.LogWarning(ex, "Automatic history import failed."); }
            finally { Interlocked.Exchange(ref _running, 0); }
        });
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }
}
