namespace MusicFinder.Domain.Enums;

/// <summary>Rows of the append-only TrackEvents log. Stored as text.</summary>
public enum TrackEventType
{
    /// <summary>The user began dragging the track out of MusicFinder (attempt only).</summary>
    DragStarted,
    /// <summary>
    /// The drag ended with a copy/link effect. This is the anchor of the inference window; the evidence records that the
    /// outcome is unknown (<c>DragCompletedUnknown</c>): the OS tells us a drop happened, not that anything was loaded or played.
    /// </summary>
    Exported,
    /// <summary>The inactivity window elapsed with no relevant activity: weak evidence of a play.</summary>
    HeuristicPossible,
    /// <summary>The user did something relevant before the window elapsed; the export stays "Exported", never "not played".</summary>
    ActivityInterrupted,
    ManualMarkPlayed,
    ManualMarkNotPlayed,
    ManualUndo,
    /// <summary>A DJ program's own history reported this play (VirtualDJ, Traktor): the evidence behind a Confirmed play.</summary>
    HistoryConfirmed
}

/// <summary>The only statuses allowed in PlayHistory (a CHECK constraint enforces it).</summary>
public enum PlayStatus { Possible, Confirmed }

/// <summary>What the user does that counts as "still working" and therefore interrupts an inference window.</summary>
public enum UserActivity
{
    /// <summary>Query text, filters, sorting or Reset changed.</summary>
    Search,
    /// <summary>The selected folder or the folder-scope toggles changed.</summary>
    Scope,
    /// <summary>The selection changed to a different, non-empty set of tracks.</summary>
    Selection,
    /// <summary>The user stopped the session; windows still open are resolved as interrupted, never as played.</summary>
    SessionStopped,
    /// <summary>The application is closing; pending windows are resolved as interrupted, never as played.</summary>
    AppClosed
}

/// <summary>The "Played" filter (pill and <c>played:</c> grammar).</summary>
public enum PlayedFilter { Never, Session, Possible, Confirmed }

/// <summary>A track's state inside the active session, for the grid icon and the Session tab.</summary>
public enum SessionPlayStatus { None, Exported, Possible, Confirmed }
