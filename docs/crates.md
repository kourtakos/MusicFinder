# Crates

A crate is a named, ordered list of library tracks, for preparing a set. It only **references** tracks: creating, changing or deleting a crate never moves, renames, retags or deletes a music file, and deleting a crate leaves the tracks in the library.

## Using crates

- **Make one:** in the *Crates* panel (left, under the folders) type a name and press Enter. The tracks selected at that moment are added to it.
- **Add tracks:** select tracks in the grid, right-click, *Add to crate*, pick the crate. A track already in the crate is skipped and the message says how many.
- **Open one:** click it in the panel. The grid shows the crate in its own order; *Close* goes back to your search. Choosing a folder, typing a search or sorting by a column also leaves the crate.
- **Take tracks out:** open the crate, select, *Remove selected* (or right-click, *Remove from this crate*). This only edits the list.
- **Drag to VirtualDJ / Traktor:** works as for any result, including several tracks at once.
- **Delete a crate:** right-click it in the panel. You are asked to confirm.

A crate name is unique regardless of case and at most 100 characters. One track can be in many crates. Tracks currently marked missing (a disconnected drive) stay in the crate but are not counted.

## Data

Tables `Crates` and `CrateTracks` (migration `0014_Crates.sql`), kept in the library database, so a library backup includes them.

## Not yet

Ordering a crate by BPM/key, manual reordering, `.m3u8` export and saved searches are planned next.
