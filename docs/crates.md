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

## Ordering and export

With a crate open:

- **Sort by BPM** puts it slowest first. Tracks with no BPM stay at the end.
- **Harmonic order** suggests a running order: it starts at the slowest track that has a BPM and a readable key, then repeatedly takes the remaining track closest on the Camelot wheel (same key, relative key, neighbour; 12 and 1 are neighbours) and, among those, closest in tempo. It is a heuristic, not a verdict on what mixes. Tracks without BPM or key are never given made-up values; they go last.
- **Export .m3u8** saves the crate in its order as an extended M3U8 playlist (UTF-8, absolute paths, duration and "Artist - Title"). Files marked missing are left out and the message says how many. Windows asks before replacing an existing file. Whether a DJ program imports it is up to that program; MusicFinder has not verified VirtualDJ or Traktor import.

Sorting rewrites only the crate's own order; it cannot be undone, but nothing outside the list changes.

## Not yet

Manual reordering and saved searches are planned next.
