using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Entities;
using MusicFinder.Domain.Enums;
using MusicFinder.Domain.Models;

namespace MusicFinder.Application.Dj;

/// <inheritdoc cref="ISessionService"/>
public sealed class DjSessionService : ISessionService, IPlayHistoryQuery, IImportedPlayRecorder
{
    /// <summary>Quiet this long inside a session and the next drag starts a new one instead of extending an unrelated night.</summary>
    public static readonly TimeSpan SessionGap = TimeSpan.FromHours(4);
    private const int UndoLimit = 20;

    private readonly IDjSessionRepository _sessions;
    private readonly ITrackEventRepository _events;
    private readonly IPlayHistoryRepository _plays;
    private readonly ISettingsStore _settings;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly PlayInferenceEngine _engine;
    private readonly ITimer _timer;

    /// <summary>Serializes everything that writes, so events land in the order things happened.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<Task, byte> _background = new();
    private readonly LinkedList<UndoStep> _undo = new();
    private readonly object _stateLock = new();
    private DjSession? _active;
    private bool _shutDown;

    private sealed record UndoStep(string Label, long TrackId, Func<Task> Revert);

    public event EventHandler? SessionChanged;
    public event EventHandler<IReadOnlyList<long>>? PlayStateChanged;

    public DjSessionService(IDjSessionRepository sessions, ITrackEventRepository events, IPlayHistoryRepository plays,
        ISettingsStore settings, TimeProvider time, ILogger<DjSessionService>? log = null)
    {
        _sessions = sessions;
        _events = events;
        _plays = plays;
        _settings = settings;
        _time = time;
        _log = log ?? NullLogger<DjSessionService>.Instance;
        _engine = new PlayInferenceEngine(time);
        _timer = time.CreateTimer(_ => OnTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public DjSession? Active
    {
        get { lock (_stateLock) return _active; }
    }

    public bool CanUndo
    {
        get { lock (_stateLock) return _undo.Count > 0; }
    }

    private DateTime NowUtc => _time.GetUtcNow().UtcDateTime;

    // ===== sessions =====

    public async Task InitializeAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // A session left open by a crash is not "live" any more; close it at its last sign of life.
            foreach (var stale in (await _sessions.GetRecentAsync(20, ct)).Where(s => s.EndedUtc is null))
            {
                var last = await _sessions.GetLastEventUtcAsync(stale.Id, ct) ?? stale.StartedUtc;
                await _sessions.EndAsync(stale.Id, last, ct);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<long> StartAsync(string? djSoftware, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (Active is { } live) return live.Id;
            var session = await StartInternalAsync(djSoftware, autoStarted: false, ct);
            return session.Id;
        }
        finally { _gate.Release(); }
    }

    private async Task<DjSession> StartInternalAsync(string? djSoftware, bool autoStarted, CancellationToken ct)
    {
        var now = NowUtc;
        var name = "Session " + _time.GetLocalNow().ToString("dd/MM HH:mm", CultureInfo.InvariantCulture);
        var id = await _sessions.StartAsync(now, djSoftware, name, autoStarted, ct);
        var session = (await _sessions.GetAsync(id, ct))!;
        lock (_stateLock) _active = session;
        SessionChanged?.Invoke(this, EventArgs.Empty);
        return session;
    }

    public async Task RenameAsync(string? name, string? djSoftware, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var session = Active;
            if (session is null) return;
            await _sessions.UpdateAsync(session.Id, djSoftware, string.IsNullOrWhiteSpace(name) ? session.Name : name.Trim(), ct);
            var updated = await _sessions.GetAsync(session.Id, ct);
            lock (_stateLock) _active = updated;
            SessionChanged?.Invoke(this, EventArgs.Empty);
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (Active is null) return;
            await ResolveInterruptedAsync(_engine.Elapsed(), _engine.Interrupt(UserActivity.SessionStopped), UserActivity.SessionStopped, ct);
            await EndActiveAsync(NowUtc, ct);
        }
        finally { _gate.Release(); }
    }

    private async Task EndActiveAsync(DateTime endedUtc, CancellationToken ct)
    {
        DjSession? session;
        lock (_stateLock) { session = _active; _active = null; }
        if (session is null) return;
        await _sessions.EndAsync(session.Id, endedUtc, ct);
        Rearm();
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    // ===== drags =====

    public Task RecordDragAsync(long sessionId, IReadOnlyList<long> trackIds, CancellationToken ct)
    {
        var now = NowUtc;
        return RecordDragCoreAsync(new DragRecord(trackIds, now, now, "Copy"), sessionId, ct);
    }

    public Task RecordDragAsync(DragRecord drag, CancellationToken ct) => RecordDragCoreAsync(drag, explicitSessionId: null, ct);

    private async Task RecordDragCoreAsync(DragRecord drag, long? explicitSessionId, CancellationToken ct)
    {
        // Esc during the drag, or a drop nobody accepted: nothing happened, so nothing is recorded.
        if (drag.TrackIds.Count == 0 || string.Equals(drag.Effect, "None", StringComparison.OrdinalIgnoreCase)) return;

        await _gate.WaitAsync(ct);
        try
        {
            var session = explicitSessionId is { } id
                ? await _sessions.GetAsync(id, ct)
                : await SessionForDragAsync(drag.CompletedUtc, ct);
            var window = PlayInferenceEngine.ClampWindow(_settings.Load().SessionHeuristicSeconds);
            var batch = Guid.NewGuid().ToString("N");
            var durationMs = (int)Math.Max(0, (drag.CompletedUtc - drag.StartedUtc).TotalMilliseconds);

            // Per track, two rows: the attempt (DragStarted) and the export. The export carries the outcome as evidence:
            // the OS says a drop was accepted, not that anything was loaded or played.
            var rows = new List<TrackEvent>();
            for (var i = 0; i < drag.TrackIds.Count; i++)
            {
                rows.Add(new TrackEvent
                {
                    TrackId = drag.TrackIds[i], SessionId = session?.Id, EventType = nameof(TrackEventType.DragStarted),
                    OccurredUtc = drag.StartedUtc, Source = PlaySources.DragExport,
                    EvidenceJson = Json(new { batch, index = i, count = drag.TrackIds.Count })
                });
                rows.Add(new TrackEvent
                {
                    TrackId = drag.TrackIds[i], SessionId = session?.Id, EventType = nameof(TrackEventType.Exported),
                    OccurredUtc = drag.CompletedUtc, Source = PlaySources.DragExport,
                    EvidenceJson = Json(new
                    {
                        batch, index = i, count = drag.TrackIds.Count, effect = drag.Effect, durationMs,
                        foreground = drag.ForegroundProcess, outcome = "DragCompletedUnknown", windowSeconds = (int)window.TotalSeconds
                    })
                });
            }

            var ids = await _events.AddAsync(rows, ct);
            for (var i = 0; i < drag.TrackIds.Count; i++)
                _engine.Open(ids[i * 2 + 1], drag.TrackIds[i], drag.CompletedUtc, window, session?.Id); // one window per track per drag

            Rearm();
            RaisePlayState(drag.TrackIds);
        }
        finally { _gate.Release(); }
    }

    /// <summary>The live session, or a new one (when allowed), never silently extending an old night.</summary>
    private async Task<DjSession?> SessionForDragAsync(DateTime nowUtc, CancellationToken ct)
    {
        var active = Active;
        if (active is not null)
        {
            var last = await _sessions.GetLastEventUtcAsync(active.Id, ct) ?? active.StartedUtc;
            if (nowUtc - last <= SessionGap) return active;
            await EndActiveAsync(last, ct); // a different night: close the old one where it really stopped
        }
        return _settings.Load().AutoStartSessionOnDrag ? await StartInternalAsync(null, autoStarted: true, ct) : null;
    }

    // ===== inference =====

    public void NotifyActivity(UserActivity activity)
    {
        // Elapsed first: a window that already ran its full length must become a Possible play even if the timer was late.
        var elapsed = _engine.Elapsed();
        var interrupted = _engine.Interrupt(activity);
        if (elapsed.Count == 0 && interrupted.Count == 0) return;
        Rearm(); // nothing is pending any more, so the timer has nothing left to wait for

        Run(async () =>
        {
            await _gate.WaitAsync();
            try { await ResolveInterruptedAsync(elapsed, interrupted, activity, CancellationToken.None); }
            finally { _gate.Release(); }
        });
    }

    private void OnTimer() => Run(async () =>
    {
        await _gate.WaitAsync();
        try { await ResolveInterruptedAsync(_engine.Elapsed(), [], UserActivity.Search, CancellationToken.None); }
        finally { _gate.Release(); }
        Rearm();
    });

    /// <summary>Writes the outcome of windows that ended: full quiet => Possible; activity => stays Exported.</summary>
    private async Task ResolveInterruptedAsync(IReadOnlyList<PendingExport> elapsed, IReadOnlyList<PendingExport> interrupted,
        UserActivity activity, CancellationToken ct)
    {
        if (elapsed.Count == 0 && interrupted.Count == 0) return;
        var now = NowUtc;
        var tracks = new List<long>();

        foreach (var p in elapsed)
        {
            await _events.AddAsync([new TrackEvent
            {
                TrackId = p.TrackId, SessionId = p.SessionId, EventType = nameof(TrackEventType.HeuristicPossible), OccurredUtc = now,
                Source = PlaySources.InactivityHeuristic, Confidence = 0.3,
                EvidenceJson = Json(new { anchorEventId = p.AnchorEventId, quietSeconds = (int)p.Window.TotalSeconds })
            }], ct);

            // ExternalEventKey makes this idempotent: resolving the same export twice cannot count it twice.
            await _plays.AddAsync(new PlayRecord
            {
                TrackId = p.TrackId, SessionId = p.SessionId, PlayedUtc = p.ExportedUtc, Status = nameof(PlayStatus.Possible),
                Source = PlaySources.InactivityHeuristic, ExternalEventKey = $"evt:{p.AnchorEventId}", RelatedEventId = p.AnchorEventId
            }, ct);
            tracks.Add(p.TrackId);
        }

        foreach (var p in interrupted)
        {
            var atSeconds = Math.Max(0, (int)Math.Round((now - p.ExportedUtc).TotalSeconds));
            await _events.AddAsync([new TrackEvent
            {
                TrackId = p.TrackId, SessionId = p.SessionId, EventType = nameof(TrackEventType.ActivityInterrupted), OccurredUtc = now,
                Source = PlaySources.DragExport,
                EvidenceJson = Json(new { anchorEventId = p.AnchorEventId, activity = activity.ToString(), atSeconds })
            }], ct);
            tracks.Add(p.TrackId);
        }

        RaisePlayState(tracks);
    }

    private void Rearm()
    {
        if (_shutDown) return;
        try { _timer.Change(_engine.TimeToNextDeadline ?? Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { /* shutting down */ }
    }

    // ===== manual decisions =====

    public Task ConfirmPlayAsync(long sessionId, long trackId, DateTimeOffset playedAt, CancellationToken ct) =>
        MarkPlayedCoreAsync(trackId, anchorEventId: null, sessionId, playedAt.UtcDateTime, ct);

    public Task MarkPlayedAsync(long trackId, long? anchorEventId, CancellationToken ct) =>
        MarkPlayedCoreAsync(trackId, anchorEventId, sessionId: null, playedUtc: null, ct);

    private async Task MarkPlayedCoreAsync(long trackId, long? anchorEventId, long? sessionId, DateTime? playedUtc, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var anchor = await ResolveAnchorAsync(trackId, anchorEventId, ct);
            if (anchor is not null) _engine.Cancel(anchor.Id);

            // The new Confirmed play replaces the Possible/manual one it grew out of, so the track counts once, not twice.
            var removed = anchor is null ? [] : await _plays.DeleteByAnchorAsync(anchor.Id, ct);
            var key = anchor is null ? $"manual:{Guid.NewGuid():N}" : $"evt:{anchor.Id}";
            var session = sessionId ?? anchor?.SessionId ?? Active?.Id;
            var at = playedUtc ?? anchor?.OccurredUtc ?? NowUtc;

            var newId = await _plays.AddAsync(new PlayRecord
            {
                TrackId = trackId, SessionId = session, PlayedUtc = at, Status = nameof(PlayStatus.Confirmed),
                Source = PlaySources.User, ExternalEventKey = key, RelatedEventId = anchor?.Id
            }, ct);

            await _events.AddAsync([new TrackEvent
            {
                TrackId = trackId, SessionId = session, EventType = nameof(TrackEventType.ManualMarkPlayed), OccurredUtc = NowUtc,
                Source = PlaySources.User, Confidence = 1.0,
                EvidenceJson = Json(new { anchorEventId = anchor?.Id, replaced = removed.Select(r => r.Status).ToArray() })
            }], ct);

            Push(new UndoStep("Mark as played", trackId, async () =>
            {
                if (newId is { } id) await _plays.DeleteAsync(id);
                foreach (var r in removed) await _plays.RestoreAsync(r);
            }));
            RaisePlayState([trackId]);
        }
        finally { _gate.Release(); }
    }

    public async Task MarkNotPlayedAsync(long trackId, long? anchorEventId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var anchor = await ResolveAnchorAsync(trackId, anchorEventId, ct);
            var wasPending = anchor is not null && _engine.Cancel(anchor.Id);

            var removed = anchor is not null
                ? await _plays.DeleteByAnchorAsync(anchor.Id, ct)
                : await _plays.DeleteLatestRemovableAsync(trackId, ct);
            if (removed.Count == 0 && !wasPending && anchor is null) return; // nothing to undo, nothing to say

            await _events.AddAsync([new TrackEvent
            {
                TrackId = trackId, SessionId = anchor?.SessionId ?? Active?.Id, EventType = nameof(TrackEventType.ManualMarkNotPlayed),
                OccurredUtc = NowUtc, Source = PlaySources.User, Confidence = 1.0,
                EvidenceJson = Json(new { anchorEventId = anchor?.Id, removed = removed.Count })
            }], ct);

            // Undo puts the plays back. A window that was still pending is not reopened: the user had decided.
            Push(new UndoStep("Mark as not played", trackId, async () => { foreach (var r in removed) await _plays.RestoreAsync(r); }));
            RaisePlayState([trackId]);
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> UndoAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            UndoStep? step;
            lock (_stateLock)
            {
                step = _undo.Last?.Value;
                if (step is not null) _undo.RemoveLast();
            }
            if (step is null) return false;

            await step.Revert();
            await _events.AddAsync([new TrackEvent
            {
                TrackId = step.TrackId, SessionId = Active?.Id, EventType = nameof(TrackEventType.ManualUndo), OccurredUtc = NowUtc,
                Source = PlaySources.User, EvidenceJson = Json(new { undid = step.Label })
            }], ct);
            RaisePlayState([step.TrackId]);
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>The export a manual decision refers to: the one given, else the track's latest in the live session.</summary>
    private async Task<TrackEvent?> ResolveAnchorAsync(long trackId, long? anchorEventId, CancellationToken ct)
    {
        if (anchorEventId is { } id) return await _events.GetAsync(id, ct);
        var session = Active;
        if (session is null) return null;
        return (await _events.GetAnchorsForTrackAsync(session.Id, trackId, ct)).FirstOrDefault();
    }

    private void Push(UndoStep step)
    {
        lock (_stateLock)
        {
            _undo.AddLast(step);
            while (_undo.Count > UndoLimit) _undo.RemoveFirst();
        }
    }

    // ===== imported history =====

    /// <summary>
    /// A drag comes before the load, which comes before the entry in the DJ program's history; allow for a track parked on a deck for a while.
    /// A history time a little before the drag is still accepted: clocks and the program's own stamping differ by seconds.
    /// </summary>
    public static readonly TimeSpan ImportMatchBefore = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan ImportMatchAfter = TimeSpan.FromMinutes(2);

    /// <inheritdoc cref="IImportedPlayRecorder.RecordImportedPlayAsync"/>
    public async Task<ImportedPlayOutcome> RecordImportedPlayAsync(
        ImportedPlayEvent play, long trackId, string matchedBy, double confidence, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var from = play.PlayedUtc - ImportMatchBefore;
            var to = play.PlayedUtc + ImportMatchAfter;
            TimeSpan Gap(DateTime t) => (t - play.PlayedUtc).Duration();

            // Read first, change after: a play imported before changes nothing, so nothing may be replaced for it.
            var target = (await _plays.FindUpgradableAsync(trackId, from, to, ct)).OrderBy(p => Gap(p.PlayedUtc)).FirstOrDefault();
            var anchorId = target?.RelatedEventId;
            if (target is null)
                anchorId = _engine.Pending.Where(p => p.TrackId == trackId && p.ExportedUtc >= from && p.ExportedUtc <= to)
                    .OrderBy(p => Gap(p.ExportedUtc)).FirstOrDefault()?.AnchorEventId;
            var anchor = anchorId is { } id ? await _events.GetAsync(id, ct) : null;
            var sessionId = target?.SessionId ?? anchor?.SessionId ?? await SessionAtAsync(play.PlayedUtc, ct);

            var confirmedId = await _plays.AddAsync(new PlayRecord
            {
                TrackId = trackId, SessionId = sessionId, PlayedUtc = play.PlayedUtc, Status = nameof(PlayStatus.Confirmed),
                Source = play.Source, ExternalEventKey = play.ExternalEventKey, RelatedEventId = anchor?.Id
            }, ct);
            if (confirmedId is not { } newId) return ImportedPlayOutcome.Duplicate;

            if (target is not null) await _plays.MarkSupersededAsync(target.Id, newId, ct);
            if (anchor is not null) _engine.Cancel(anchor.Id);

            await _events.AddAsync([new TrackEvent
            {
                TrackId = trackId, SessionId = sessionId, EventType = nameof(TrackEventType.HistoryConfirmed), OccurredUtc = play.PlayedUtc,
                Source = play.Source, Confidence = confidence,
                EvidenceJson = Json(new
                {
                    file = play.FilePath, key = play.ExternalEventKey, matchedBy, anchorEventId = anchor?.Id, supersededPlayId = target?.Id
                })
            }], ct);

            RaisePlayState([trackId]);
            return target is null && anchor is null ? ImportedPlayOutcome.Added : ImportedPlayOutcome.Upgraded;
        }
        finally { _gate.Release(); }
    }

    /// <summary>The session that was running at that time, if any. Importing never creates a session.</summary>
    private async Task<long?> SessionAtAsync(DateTime utc, CancellationToken ct)
    {
        foreach (var s in await _sessions.GetRecentAsync(20, ct))
            if (s.StartedUtc <= utc && utc <= (s.EndedUtc ?? DateTime.MaxValue)) return s.Id;
        return null;
    }

    // ===== queries =====

    public async Task<IReadOnlyList<SessionEntry>> GetEntriesAsync(long sessionId, CancellationToken ct)
    {
        var rows = await _plays.GetSessionEntriesAsync(sessionId, ct);
        return rows.Select(SessionEntryBuilder.Build).ToList();
    }

    public Task<IReadOnlyList<PlayHistoryItem>> GetForTrackAsync(long trackId, int limit, CancellationToken ct) =>
        _plays.GetForTrackAsync(trackId, limit, ct);

    // ===== lifetime =====

    public async Task ShutdownAsync()
    {
        if (_shutDown) return;
        await _gate.WaitAsync();
        try
        {
            _shutDown = true;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            await ResolveInterruptedAsync(_engine.Elapsed(), _engine.Interrupt(UserActivity.AppClosed), UserActivity.AppClosed, CancellationToken.None);
            await EndActiveAsync(NowUtc, CancellationToken.None);
        }
        finally { _gate.Release(); }
        await WaitForIdleAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync();
        await _timer.DisposeAsync();
    }

    /// <summary>Waits for the background work started by timers and activity notifications (tests and shutdown).</summary>
    public async Task WaitForIdleAsync()
    {
        while (true)
        {
            var running = _background.Keys.ToArray();
            if (running.Length == 0) return;
            await Task.WhenAll(running);
        }
    }

    // ===== helpers =====

    private void Run(Func<Task> work)
    {
        var task = Task.Run(async () =>
        {
            try { await work(); }
            catch (Exception ex) { _log.LogError(ex, "Session background work failed."); } // never let a timer callback throw
        });
        _background[task] = 0;
        _ = task.ContinueWith(t => _background.TryRemove(t, out _), TaskScheduler.Default);
    }

    private void RaisePlayState(IReadOnlyList<long> trackIds)
    {
        if (trackIds.Count > 0) PlayStateChanged?.Invoke(this, trackIds.Distinct().ToList());
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);
}
