using MusicFinder.Domain.Enums;

namespace MusicFinder.Application.Dj;

/// <summary>An export waiting to see whether the user stays quiet for its whole window.</summary>
/// <param name="OpenedTimestamp">Monotonic <see cref="TimeProvider.GetTimestamp"/> value when the window opened.</param>
/// <param name="ExportedUtc">Wall-clock time of the export; this is what a resulting Possible play is stamped with.</param>
/// <param name="Window">The window length captured when it opened (changing the setting affects only later drags).</param>
public sealed record PendingExport(
    long AnchorEventId, long TrackId, long OpenedTimestamp, DateTime ExportedUtc, TimeSpan Window, long? SessionId = null);

/// <summary>
/// The CLAUDE.md section 9 heuristic, with no I/O. Each export opens its own independent window. A window that runs its
/// full length without relevant user activity becomes a Possible play (weak evidence, never proof). Activity before the
/// deadline resolves the window as "interrupted": the export stays Exported, it is never turned into "not played".
/// Deadlines use the monotonic clock so a wall-clock change or sleep cannot shorten a window.
/// </summary>
public sealed class PlayInferenceEngine
{
    public static readonly TimeSpan MinWindow = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan MaxWindow = TimeSpan.FromSeconds(20);

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly List<PendingExport> _pending = [];

    public PlayInferenceEngine(TimeProvider time) => _time = time;

    /// <summary>Clamps a configured number of seconds to the allowed 15 - 20 s range.</summary>
    public static TimeSpan ClampWindow(int seconds) =>
        TimeSpan.FromSeconds(Math.Clamp(seconds, (int)MinWindow.TotalSeconds, (int)MaxWindow.TotalSeconds));

    public IReadOnlyList<PendingExport> Pending
    {
        get { lock (_gate) return [.. _pending]; }
    }

    /// <summary>Starts a window for one exported track. The same track exported twice gets two windows.</summary>
    public void Open(long anchorEventId, long trackId, DateTime exportedUtc, TimeSpan window, long? sessionId = null)
    {
        var clamped = ClampWindow((int)Math.Round(window.TotalSeconds));
        lock (_gate) _pending.Add(new PendingExport(anchorEventId, trackId, _time.GetTimestamp(), exportedUtc, clamped, sessionId));
    }

    /// <summary>Removes and returns windows whose full length has passed. Cheap; safe to call at any time.</summary>
    public IReadOnlyList<PendingExport> Elapsed()
    {
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            var done = _pending.Where(p => _time.GetElapsedTime(p.OpenedTimestamp, now) >= p.Window).ToList();
            foreach (var d in done) _pending.Remove(d);
            return done;
        }
    }

    /// <summary>
    /// Relevant activity: every window still open is resolved as interrupted. Callers must call <see cref="Elapsed"/> first,
    /// so a window that already ran its full length is not lost just because the timer had not ticked yet.
    /// </summary>
    public IReadOnlyList<PendingExport> Interrupt(UserActivity activity)
    {
        lock (_gate)
        {
            var all = _pending.ToList();
            _pending.Clear();
            return all;
        }
    }

    /// <summary>Drops one window without resolving it (the user marked the track manually).</summary>
    public bool Cancel(long anchorEventId)
    {
        lock (_gate) return _pending.RemoveAll(p => p.AnchorEventId == anchorEventId) > 0;
    }

    /// <summary>Time until the earliest window ends (zero when one is already due), or null when nothing is pending.</summary>
    public TimeSpan? TimeToNextDeadline
    {
        get
        {
            lock (_gate)
            {
                if (_pending.Count == 0) return null;
                var now = _time.GetTimestamp();
                var soonest = _pending.Min(p => p.Window - _time.GetElapsedTime(p.OpenedTimestamp, now));
                return soonest < TimeSpan.Zero ? TimeSpan.Zero : soonest;
            }
        }
    }
}
