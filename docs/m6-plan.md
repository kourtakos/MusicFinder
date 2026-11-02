# M6 plan: organize, duplicates, Library Health

Slices, in order. Each ends with build and tests green, docs, and a push. All tests that touch files use generated copies in temp folders, never a real library.

| Slice | Feature | Writes to music files? | Status |
|---|---|---|---|
| 1 | Library Health tab, quality facts, new search filters | No | **Done** (`docs/library-health.md`) |
| 2 | Exact duplicates: streaming SHA-256 on same-size files, hash cache, scan with cancel and resume, review window, `dup:` filter | No | **Done** (`docs/duplicates.md`) |
| 3 | Batch rename in place: template, sanitizer, preview that is the confirmation, journal before every move, crash recovery, undo, CSV path map, DJ-software warning | Rename only | **Done** (`docs/rename.md`) |
| 4 | Move/organize into a folder template (same volume). Cross-volume copy-verify-delete is opt-in and deferred | Yes | **Done** (`docs/rename.md`) |
| 5 | Probable same recording and other versions (remix, edit, live), shown apart, never merged | No | **Done** (`docs/probable-duplicates.md`) |

## Decisions already taken

- `{Genre}` in templates is the taxonomy's top genre, falling back to the raw tag. `.lrc` and `.cue` files beside a track are renamed with it (default on); `cover.jpg`/`folder.jpg` are never moved.
- A track with a missing tag used in the template is **skipped** with a visible reason; "Unknown Artist" placeholders are a checkbox, off by default.
- No automatic deletion anywhere. Duplicates are only reported; actions are Open in Explorer and Show in Library. "Suggested keep" is a suggestion with reasons.
- Health tab name: **Health**. Mojibake is counted, not repaired (a prefilled repair in the tag editor is a possible later step).

## Design facts that shape the work

- There is **no file watcher**. The scanner upserts by `FullPath`, so a moved file would become "old row Missing + new row": a move must **UPDATE the existing track row** (path, name, folder, size, mtime) in the same transaction that marks the journal row done, so play history, genres, DJ tags, analysis and identification rows (all keyed by `Tracks.Id`) follow the track. The FTS and alias triggers already react to a path change.
- Scans and file operations must not run together (a write gate); crash recovery of the journal runs at startup before the rescan.
- Migrations: 0009 quality/health (done), 0010 duplicates (`FileHashes` keyed by track + size + mtime, `DuplicateScans/Groups/Members/Dismissals`), 0011 file-operation journal (`FileOperationBatches`, `FileOperations`; the unused 0001 table is dropped and recreated).
- **Traktor and VirtualDJ key their own databases by file path.** Renaming or moving files outside them breaks cue points, grids and playlists there. The app shows a prominent first-use warning, repeats a reminder in every preview, and exports an old-to-new path map (CSV, UTF-8 with BOM). Whether those programs' relocate features accept the map is unverified.

## Slice 2 (exact duplicates), steps

1. Migration 0010, `IFileHashService` (`StreamingFileHasher`: shared read, sequential scan, 1 MB chunks, cancellable per chunk, throttled to a setting) and `IFileHashStore`. Tests: known SHA-256, cache hit and invalidation, cancellation leaves no row, locked file reported not thrown.
2. `IDuplicateScanService` (size buckets, hash only members without a valid cache row, groups persisted, dismissals survive a re-scan, resume after cancel), `KeepSuggestion` (lossless over lossy, bitrate, tag completeness, cover, older index time). Tests: same bytes different names, same size different bytes, only new files re-hashed, nothing deleted.
3. `DuplicatesWindow` from the Health tab (progress, groups with members, Open in Explorer, Show in Library, "Not a duplicate"), `dup:` filter, UI tests including "no delete command exists".

## Slice 3 (rename), the essentials

`FileNameTemplate` (`{Artist} {Title} {Album} {AlbumArtist} {Year} {Track:00} {Genre} {Bpm} {Key} {Extension} {FileName}`, case modifiers only when asked), `PathSanitizer` (invalid characters, reserved device names including with an extension, trailing dots and spaces, 255-segment and 259-path limits; Unicode kept as is), `FileOperationPlanner` (unchanged, case-only, collisions on disk and inside the batch, offline roots, sidecars), `FileOperationExecutor` (journal row `InProgress` before each move; destination re-checked; `File.Move` without overwrite; row update plus journal `Done` in one transaction; failures do not stop the batch), `RecoverAsync` at startup, undo of a whole batch in reverse with per-file refusals (file changed since, old path occupied, file gone). The preview table **is** the confirmation, with one button and a "Will rename N files" line.

## Open for later

Cross-volume moves (copy, verify hash, delete the source only with explicit approval), probable-duplicate tuning (version tokens, featured-artist parsing, phonetic artist bridge), a Recycle Bin action for duplicates (explicit, per file, out of M6's first cut).
