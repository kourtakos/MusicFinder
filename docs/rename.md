# Batch rename (M6, slice 3)

Select tracks in the Library, then **Rename files…** (context menu or F6). The window shows a preview of every file; the preview is the confirmation, and the one button says how many files it will rename. Files are renamed inside their own folder. Nothing is overwritten, deleted or moved to another folder.

## Template
`{Artist} {Title} {Album} {AlbumArtist} {Year} {Track:00} {Genre} {Bpm} {Key} {FileName} {Extension}`. Case is changed only when asked: `{Artist:upper}`, `:lower`, `:title`. The extension is kept automatically unless the template has `{Extension}`. `{Genre}` is the top genre of the taxonomy, or the raw tag when the track has no mapped genre. Characters Windows does not allow (`< > : " / \ | ? *`) become `_`; trailing dots and spaces, reserved names (`CON`, `NUL`, `COM1`…, also with an extension), names over 255 characters and paths over 259 characters are handled or skipped with a reason. Greek and other Unicode text is kept as it is.

## What gets skipped, with a visible reason
A tag the template needs is empty (or tick "Use Unknown Artist/Title… when a tag is missing"); the new name already exists on disk; two files in the batch get the same name; a `.lrc`/`.cue` next to the track would collide; the file is not where the library says (offline drive); the path would be too long. A name that only differs in case is a **case-only** rename; a name that is already right is left alone. `.lrc` and `.cue` files with the same name travel with the track; `cover.jpg` and `folder.jpg` never move.

## Safety
- Every file: re-check, write the journal row `InProgress`, `File.Move` **without overwrite**, then mark `Done` together with the database row. A failure (locked file, a file that appeared since the preview) is recorded for that file and the batch goes on.
- A scan and a rename never run at the same time. A rename refuses to start while a scan runs; a scan waits for a running rename.
- If the app dies in the middle, the next start settles the half-done operations before the first scan: a move that happened is completed in the database, a move that did not happen is marked failed, and anything unclear (both files exist, neither exists) is shown to the user, not guessed.
- **Undo** a whole batch from "Earlier renames", newest file first. A file is only restored if it is still where the rename left it, the old name is free, and the library has not renamed it again since. Anything else is refused with a reason and can be retried.
- Before the first rename a warning explains that **Traktor and VirtualDJ remember files by path**: renaming a file that is in their library loses cue points, grids and playlist entries there until it is relocated. MusicFinder cannot change those programs. "Save old and new paths…" writes a CSV (`OldPath,NewPath`, UTF-8 with BOM) for every batch. Whether the programs' own relocate features can use such a list is not verified.

## Tests
Unit: template, sanitizer, planner (`tests/MusicFinder.UnitTests/Organize`). Integration on generated files in temp folders: journal order, collisions, locked files, crash before and after the move, ambiguous recovery, undo and its refusals, case-only rename on NTFS, scan lock, cancel, CSV. UI: preview, warning once, rename, undo, CSV, missing tags. No test touches a real music library.

## Move into folders (slice 4)
Tick **Move into folders below the library folder** in the same window. The template then describes the path below the track's library folder, with `/` between folders, for example `{Genre}/{Artist}/{Artist} - {Title}` or `{Genre}/{Artist}/{Album}/{Track:00} {Title}`. The preview shows each file's old and new path relative to its library folder; the button says "Move N files".
- Every folder name is sanitized like a file name, so a tag value can never create or escape a folder (`AC/DC` becomes `AC_DC`, `..` becomes `_`). Files stay inside their library folder, on the same volume. Moving to another drive (copy, verify, delete) is not offered.
- Missing folders are created. The track's database row gets its new folder (rows are created for new folders, the tree counts are refreshed), so a later scan finds nothing new and nothing missing.
- Same journal, recovery, collision checks, sidecars and undo as a rename. **Undo moves the files back but never removes folders**, so empty folders can be left behind; MusicFinder deletes nothing.
- Same warning about Traktor and VirtualDJ: moving a file breaks their entries for it just like renaming.
