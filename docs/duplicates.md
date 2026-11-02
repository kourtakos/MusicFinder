# Duplicate files

**Health tab, Find duplicate files…** looks for files with identical content. It only reads files: it never deletes, moves, renames or retags anything, and the window has no such command.

## How it works

1. Files are first grouped by **size** (free: the sizes are already in the library). A file whose size is unique cannot have an identical twin, so it is never read.
2. Only the files that share their size with another file are hashed (SHA-256 of the bytes), one at a time, read at no more than the speed set in `HashThrottleMbPerSec` (default 40 MB/s, 0 = no limit), and cancellable at any moment (Stop, or closing the window).
3. Each hash is remembered with the file's size. A repeat scan, or one continued after Stop, hashes only files that are new or changed. Any change to a file's size or modified time (a tag write, a replaced file) makes the database drop its hash;
   a rename or move does not, because the bytes did not change.
4. Files with the same hash form a group. Hashing is never done while indexing.

Hashing needs only shared read access, so a file that Traktor or VirtualDJ has open is still read. A file that vanished, changed while it was read, or cannot be opened is counted as "could not be read" and the scan goes on.

## What you see

Each group lists its files (folder, format, bitrate, plays) with **Open in Explorer**, **List in Library** (the Library filtered to that group, `dup:<hash start>`) and **Not a duplicate** (the group is hidden, also on later scans).
**Show all in Library** lists every file that has a twin (`dup:exact`). The Health tab has an *Exact duplicates* card with the number of files.

**Suggested to keep** is only a hint. Identical files have identical audio and tags, so quality cannot tell them apart; what can is the history the library holds for each copy (plays), whether a name looks like a copy
(`Song (1).mp3`, `Song - Copy.mp3`, `Copy of Song.mp3`), and which sits nearest the top of the folder tree. The reason is shown under the group.

## Things to know

- Byte-identical is strict: the same song with different tags, a different bitrate or a different edit is **not** a group here. Those are the "probable same recording" and "other version" kinds, coming in a later step.
- If your DJ software has these files in playlists, crates or cue-point databases, removing one of them there breaks that entry (those programs key their data by file path). That is why MusicFinder only shows and never removes.
- A track whose play history is on the copy you remove loses nothing in MusicFinder only if you keep that copy; the suggestion favours it.
- Data: `FileHashes` and `DuplicateDismissals` (migration 0010, see `database.md`). "Not a duplicate" decisions are kept by content hash.
