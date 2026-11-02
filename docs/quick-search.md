# Quick search

A small search window that opens over any program with a keyboard shortcut, so you can find a track and drag it into your DJ
software or player without bringing the MusicFinder window to the front.

## Using it

1. Press **Ctrl+Alt+Space** (the default) from any program. A search box opens on the screen your mouse is on, near the top, above all other windows.
2. Type. Results from your whole library appear below (the folder you picked in the main window is ignored). Each row shows the cover, title,
   artist, BPM, key and, if a DJ session is running, a chip with that track's status in it (Exported / Possible / Played).
3. **Drag a result** straight into VirtualDJ, Traktor or any player that accepts files. Select several with Ctrl/Shift first to drag them together.
   The drag is recorded in the DJ session exactly like a drag from the library, and typing here counts as search activity for the
   "possibly played" heuristic (see `docs/dj-session.md`).
4. **Move the window** by dragging its top bar, so it never covers your player. It remembers where you left it, and it stays open when you click
   or drop into another program (it does not hide on focus loss, because the next drag needs it).
5. Press the shortcut again, **Esc**, or the ✕ to hide it. **Enter** opens the same search in the main library window. `Ctrl+C` copies the selected file paths.
   `Delete` does nothing: MusicFinder never deletes music files.

Press **Down** in the search box to move into the results, **Up** on the first result to go back to the box.

## Choosing your own shortcut

Settings → Quick search. Click the shortcut box and press the keys you want; nothing is typed. The shortcut is checked, registered with Windows and only
then saved. If Windows or another program already owns it you get a plain message and your previous shortcut keeps working.
*Use default* restores Ctrl+Alt+Space; the checkbox switches the feature off (the keys you chose are remembered).

Rules (`HotkeyGesture`):

- It must include **Ctrl, Alt or Win** (a shortcut with only Shift, or none, would break typing in every program).
- One key: a letter, digit, F1–F24, Space, Tab, Enter, Insert, Delete, Home, End, PageUp, PageDown or an arrow key. Esc is reserved for closing the pop-up.
- Refused up front: Ctrl+Alt+Delete, Alt+Tab, Alt+F4, Ctrl+C / V / X / Z.
- Other programs may already use a combination (Windows reports "already registered" and MusicFinder says so). The default can clash with
  an input-language or screen-reader shortcut on some machines; pick another one then.

## Limits

- The shortcut works **while MusicFinder is running**, including when its window is minimized. Closing the main window exits the app. There is no tray
  icon or start-with-Windows yet.
- A hotkey cannot be pressed over exclusive full-screen programs or elevated (administrator) windows; Windows does not deliver it there.
- Only the first 40 matches are listed; Enter shows all of them in the library.
- The pop-up window shares the library's search service and database; it adds no network access.

## How it is built

`IGlobalHotkey` / `Win32GlobalHotkey` (RegisterHotKey on a hidden message-only window; WM_HOTKEY arrives on the UI thread) → `QuickSearchService`
(toggle, settings, status text) → `QuickSearchWindow` + `QuickSearchViewModel`. Drag-out is shared with the library through `TrackDragService` and
`TrackDragGesture`. Pure logic: `HotkeyGesture` (parse/validate/canonical text/virtual-key) and `WindowPlacement` (restore position unless that monitor
is gone), both unit-tested. The tests also register real Windows hotkeys (including a conflicting registration and a synthesized key press); the real window
was checked by hand: opened with the real shortcut while Winamp had the focus, a result dragged into Winamp's playlist, the shortcut changed in Settings with real keys.
