# Tags, genres and cover art (M2)

MusicFinder never changes your music unless you ask it to, and when you do, it shows exactly what will change first.

## Genres

- **Raw genre text is never discarded.** `Tracks.GenreRaw` holds exactly what the file says and stays searchable (`genre:disco`).
- **The taxonomy** is a curated, editable tree (16 categories and about 78 sub-genres to start, with English and Greek aliases).
  It lives in your database, so you can rename, move, add and delete genres in the **Tags** tab.
- **Mapping.** Each raw genre value (split on `; / , \ |`) is matched, case- and accent-insensitively, to a genre name or alias.
  Matching is exact: "Rock" never claims "Rockabilly". The result is stored in `TrackGenres` with its provenance:
  - `Embedded`: derived from the file's genre text; recomputed whenever the taxonomy changes or the file is re-read.
  - `User`: assigned by you (the `+` next to Genre in the details panel); never touched by remapping.
- **Unmapped values** (text that matches nothing) show up in the Tags tab with how many tracks carry each. Map one to a genre
  (this creates an alias), or create it as a new genre. In the grid, unmapped genres appear as muted chips so gaps stay visible.
- **Filters.** The Genre dropdown is hierarchical: choosing a parent includes its sub-genres. `Unknown genre` (no text) and
  `Uncategorized` (has text, maps to nothing) are always available. OR within the genre facet; AND with every other filter.
- **Renaming a genre** keeps its old name as an alias, so files tagged with the old name still map to it.
- Mapping works on stored text, never on the files, so taxonomy edits apply instantly to the whole library.

## DJ tags

Your own labels (Warm Up, Peak Time, Closing, Wedding, Greek Party to start). Assign them from the details panel, for one track
or a whole selection (a dash means only some of the selected tracks have it; clicking assigns it to all). Filter by them with the
Tags dropdown. They exist only in MusicFinder's database; they are not written into your files.

## Cover art

- Covers are read from the file's embedded picture (front cover preferred). Optionally, when a file has none, `cover.jpg` /
  `folder.jpg|png` in its folder is used (Settings, off by default). Nothing is downloaded.
- Thumbnails are decoded off the UI thread, cached on disk under `Cache\Artwork` (content-addressed, so an album's tracks share
  one file; trimmed oldest-first above 200 MB) and in a small in-memory cache. Rows that scroll out of view cancel their work.
- A damaged picture shows the neutral music icon and is examined once per session, never on every scroll.
- `Tracks.MetaVersion` records which reader version produced a row. When the reader learns something new (M2: the cover hash),
  files indexed by an older version are re-read once on the next start, even though their size and date did not change.

## Editing tags

Select tracks and press **F2** (or *Edit tags…*). Only fields you change are written; fields where the selected tracks differ
show "N different values" and stay untouched unless you type a new value. A ticked field with an empty box clears that field.

Safety model, per file:

1. Values are validated first (year 1900–2100, BPM 40–300, track 0–999, no control characters). Nothing is written if any is invalid.
2. You see a **preview** of every before/after value and confirm explicitly.
3. The file is copied to a hidden temporary file next to it; the change is applied to the copy.
4. The copy is read back and **verified**: every requested field has the expected value and the embedded cover is byte-identical.
5. Only then does the copy replace the original (atomic `File.Replace` on NTFS). Any failure earlier deletes the copy and leaves
   the original untouched. Read-only files are refused (the attribute is never cleared for you); files held open by another
   program are refused after a short retry.
6. The database is updated only after the file was written, including its new size and date so the next scan does not re-read it.
   If that step fails, the file is still correct and a rescan picks it up.

Other details: new ID3 tags are written as v2.3 (UTF-16), the most widely compatible with Explorer, VirtualDJ and Traktor;
existing v2.4 tags stay v2.4. Genres are written as text, not numeric codes. Fields the editor does not manage (comments,
lyrics, cover, ...) are preserved. The editor is disabled while the library is being indexed.

**Backups** are optional (a checkbox in the preview). Copies go to `TagBackups\<date>\` in MusicFinder's data folder, never
next to your music.

Verified with generated MP3, FLAC and WAV fixtures, and manually on copies of real MP3 files (Greek text, embedded cover).
M4A, OGG and AIFF writing is not yet covered by tests; treat it as unverified.
