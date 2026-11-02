namespace MusicFinder.App.Services.Dragging;

/// <summary>
/// What the OS reported when the drag ended. <see cref="Effect"/> is "Copy", "Link", "Move" or "None" (cancelled or refused).
/// A non-"None" effect only means a target accepted the drop; it is NOT proof the target loaded or played anything.
/// </summary>
public sealed record DragOutcome(string Effect, DateTime StartedUtc, DateTime CompletedUtc, string? ForegroundProcess);

/// <summary>Starts a real OS file drag (CF_HDROP) so any Windows application can accept the files.</summary>
public interface IFileDragSource
{
    /// <summary>Blocks (modal drag loop) until the drag ends. Call on the UI thread.</summary>
    DragOutcome StartDrag(IReadOnlyList<string> paths, object source);
}
