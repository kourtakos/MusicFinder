# DJ sessions and play inference (M3)

MusicFinder lets you drag tracks to any Windows application (VirtualDJ, Traktor, a media player) using the standard
file-drop mechanism. It records what *it* can observe and labels what it only *infers*. It never claims a track was played
because it was dragged.

## What is observed, what is inferred

| Status | Meaning | How it arises |
|---|---|---|
| **Exported** | Observed: a drag ended and some window accepted the files (effect Copy or Link). Unknown: whether that program loaded or played anything. | Every accepted drag. |
| **Possibly played** | Inferred, weak: after the drop you did nothing in MusicFinder for the quiet period (15–20 s, default 20). | The quiet period runs out. Source `InactivityHeuristic`. |
| **Confirmed** | Decided by you (Mark as played) or, from M4, matched in a DJ application's own history. | Never from a drag alone. |

Rules that follow from this:

- A drag, however long the quiet period, **never** produces *Confirmed*.
- Activity inside the quiet period (a new search or filter, a folder change, selecting a different track, stopping the session,
  closing the app) **cancels the guess** and leaves the entry *Exported*. It is never turned into "not played".
- *Not played* is only ever your own decision (Mark as not played) and can be undone. It never removes a play that came from a DJ
  application's history.
- Dragging the same track twice records two exports and two possible plays. Each export has its own clock, so dragging track B
  does not cancel track A's window.
- A drag you cancel (Esc, or dropped where nothing accepts it: effect `None`) records nothing.
- The *drop target* shown next to an entry (for example `Copy → winamp`) is the program under the mouse cursor when you let go. It is a hint,
  not proof of who consumed the files.

## Sessions

- **Start session / Stop session** in the Session tab. Naming it and choosing your DJ software is optional and only for your own records.
- By default the first drag starts a session automatically (Settings → DJ session). Automatic sessions are labelled as such.
- A drag more than 4 hours after the previous activity starts a *new* session and closes the old one at its last activity: unrelated
  nights are never merged.
- A session left open by a crash is closed on the next start. Closing MusicFinder resolves unfinished windows as "interrupted by exit".
- The header shows a red **LIVE** badge while a session runs (click it to open the Session tab). The status bar shows
  `Session · Traktor · 3 exported · 1 possible · 1 played`.

## Where it shows

- **Library grid:** *Last Played* (relative time, sortable), *Plays* (sortable; hover for "N confirmed, M possible") and a small chip with the
  track's strongest status in the live session (Exported / Possible / Played).
- **Details panel:** Play History for the selected track: label, local time, session.
- **Session tab:** every export of the live session, newest first, with the reason for its status in plain language, and *Played* / *Not played*
  buttons per entry plus *Undo*.
- **Filters:** the *Played* dropdown (Never played, Played in this session, Possibly played, Confirmed played) and two switches,
  *Hide confirmed in session* and *Hide possible in session*. They combine with search, folder and other filters; the switches are remembered.
  In the search box: `played:never`, `played:session`, `played:possible`, `played:confirmed`.
- **Right-click a track:** Mark as played, Mark as not played, Undo last play change.

Times are stored in UTC and shown in local time.

## Data

Tables (migration `0004_DjSessions.sql`): `DjSessions`, `TrackEvents` (append-only log: DragStarted, Exported, HeuristicPossible,
ActivityInterrupted, ManualMarkPlayed, ManualMarkNotPlayed, ManualUndo), `PlayHistory` (only Possible or Confirmed rows; a
`SupersededByPlayId` column lets a future history importer replace a guess with a confirmation without double counting) and
`TrackPlayStats` (per-track counters and last-played times, maintained by triggers so the grid and sorts stay fast).
See `docs/database.md`.

## How the drag works

`WindowsFileDragSource` starts a standard OLE drag with a `FileDrop` (CF_HDROP) list of the real file paths and a `Preferred DropEffect`
of Copy. Only **Copy and Link** are offered, never Move, so no target can move or delete a file out of your library. Missing files are left out
(you are told how many); at most 500 files go on one drag. Pressing on a track that is already part of a multi-selection keeps the selection
so several tracks can be dragged together.

## Verified so far (observed on this PC, not inferred)

Tested with a real mouse drag from MusicFinder on copies of three MP3 files:

| Program | Result |
|---|---|
| Windows Media Player (the new Store app) | Accepts the drop (effect Link), loads the file **and starts playing it immediately**. One spot of its window (the cover-art area) refused the drop (effect None); other spots accepted it. |
| Winamp 5.9.2 | Accepts the drop (Copy, Link), adds the file to its playlist, does **not** start playing. |
| Windows Media Player Legacy | Not tested (its first-run wizard wants to become the default player). |
| Traktor, VirtualDJ | **Not tested yet.** Nothing here proves they accept the drop or what they do with it. |

The same drag therefore means different things in different programs, which is why *Possibly played* stays a weak, reviewable guess.

## Manual test checklist for Traktor / VirtualDJ

1. Start MusicFinder, add a small test folder, open the Library.
2. Drag one track onto a deck (or the browser/playlist) of the DJ program. Note whether it was accepted, loaded or started.
3. In MusicFinder: Session tab shows the entry as *Exported* with the target; wait 20 s without touching MusicFinder: it becomes *Possibly played*.
4. Repeat, but type in the search box within 20 s: the entry must stay *Exported*.
5. Drag several tracks at once (Ctrl/Shift-select): all must arrive in the DJ program in order.
6. Try a path with Greek letters and a network (UNC) path.
7. Record the DJ software and its version here and in `docs/external-integrations.md` (arrives with M4).

## Limits

- No history importer yet: nothing can become *Confirmed* except your own decision (M4 adds VirtualDJ and Traktor history files).
- The drop target is a hint from the window under the cursor.
- Audio-based detection (loopback, fingerprinting) is deliberately not part of this product.
