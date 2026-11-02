using MusicFinder.Application.Dj;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.App.Services.Dragging;

/// <param name="Outcome">What the OS reported, or null when nothing was dragged (empty selection, every file missing).</param>
/// <param name="Note">Something the user should know: files left out because they are missing, or a drag capped at the maximum.</param>
/// <param name="Recording">Completes when the drag has been written to the session log; faulted if that failed.</param>
public sealed record TrackDragResult(DragOutcome? Outcome, string? Note, Task Recording);

/// <summary>
/// Dragging tracks out to another application, shared by the library grid and the quick-search window: choose what can really be
/// dragged, run the OS drag, then record the attempt and the export. An accepted drag is never proof of playback.
/// </summary>
public sealed class TrackDragService
{
    private readonly IFileDragSource _source;
    private readonly ISessionService _session;

    public TrackDragService(IFileDragSource source, ISessionService session)
    {
        _source = source;
        _session = session;
    }

    /// <summary>Runs the modal OS drag loop on the calling (UI) thread; returns when the button is released or Esc is pressed.</summary>
    public TrackDragResult Drag(IReadOnlyList<DragCandidate> selection, object source)
    {
        var payload = DragPayloadPlanner.Plan(selection, File.Exists);

        if (payload.IsEmpty)
        {
            var note = payload.SkippedMissing.Count > 0
                ? "These files are not on disk right now, so there is nothing to drag. Rescan the folder or reconnect the drive."
                : null;
            return new TrackDragResult(null, note, Task.CompletedTask);
        }

        var outcome = _source.StartDrag(payload.Included.Select(c => c.Path).ToList(), source);

        var notes = new List<string>();
        if (payload.SkippedMissing.Count > 0) notes.Add($"{payload.SkippedMissing.Count} missing file(s) were left out.");
        if (payload.Truncated > 0) notes.Add($"Only the first {DragPayloadPlanner.DefaultCap} files were dragged.");

        var record = new DragRecord(payload.Included.Select(c => c.TrackId).ToList(), outcome.StartedUtc, outcome.CompletedUtc,
            outcome.Effect, outcome.ForegroundProcess);
        return new TrackDragResult(outcome, notes.Count > 0 ? string.Join(" ", notes) : null, RecordAsync(record));
    }

    private async Task RecordAsync(DragRecord record)
    {
        try { await _session.RecordDragAsync(record, CancellationToken.None); }
        catch (Exception ex) { Serilog.Log.Warning(ex, "A drag could not be recorded"); throw; }
    }
}
