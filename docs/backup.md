# Backup, restore and relocation (M8)

**A backup holds your index, tags, genres, DJ tags, play history, identification cache and settings. It does not contain your music files.** The app says this in Settings and in the restore window.

## Backup (Settings, Backup and restore, "Back up now…")
- The database is copied with SQLite's online backup (a consistent snapshot while the app keeps working), never by copying the live file. The snapshot is integrity-checked.
- The result is one `.musicfinder-backup.zip`: `library.db`, `settings.json`, optional `artwork/` and `manifest.json` (format version, schema version, app version, creation time, library folders with track counts, row counts, and a SHA-256 for every payload).
- The finished archive is opened again and every payload is re-hashed before the backup is reported as saved. A cancelled or failed backup leaves no file behind.
- **Secrets are left out by default**: the AcoustID key, the MusicBrainz contact and the identification consent are removed from the settings copy unless "Include the AcoustID key and MusicBrainz contact" is ticked. Cover thumbnails are included only on request (they can be rebuilt). There is no archive encryption, so treat a backup that includes secrets like any other secret file.

## Restore ("Restore…")
1. Choose a backup. It is verified first; a damaged, truncated, foreign or newer-format archive is refused with a reason.
2. The window shows what is in it next to what exists now, and one row per library folder with a box for where that folder is on this PC. A drive letter, a folder or a UNC share can be entered; the path is checked ("Found on this PC").
3. A tick box ("I understand that this replaces my current library") is required. A backup from a newer MusicFinder cannot be restored.
4. **Restore** makes a verified **safety backup** of the current library in the `Backups` folder, extracts the archive into a staging folder, upgrades its schema if it is older, applies the folder mapping and checks everything. If any step fails, nothing has changed. A scan, a running rename or an unsettled rename journal blocks it.
5. The live database is **swapped when MusicFinder restarts** ("Restart now"). The old database file stays next to the new one (`musicfinder.db.previous`), nothing is deleted. A crash or power cut during the swap is finished (or rolled back to the previous library) at the next start; each phase is written to a marker file before it acts.
6. "Go back to the library from before the last restore" restores the safety backup through the same steps.

Settings from the backup return, except what belongs to this PC: the secrets (when the backup has none) and the fpcalc path.

## Path mapping
Matching is by whole folder names, ignores case, accepts `/` or `\`, `\\?\` and `\\?\UNC\`, and applies the longest matching prefix once (so `D:\DJ Music` does not match `D:\DJ Music2`, and two drives can be swapped). Rewritten: library folders, the folder tree, every track path and the rename journal. `Tracks.Id` is never changed, so play history, genres, DJ tags, analysis and identification follow each track. A mapping that would put two folders or two tracks on the same path is refused. Files that are not found after the mapping stay in the library and are marked missing by the next scan; nothing is deleted. The search index is rebuilt once and its triggers are restored exactly.

## Not verified
Restoring onto a different PC with real removable or network drives has been tested with temp folders only; do a real relocation test (USB or NAS share) before relying on it.

## Tests
`BackupTests` (live WAL snapshot while writing, hashes, secrets, artwork, damaged archives), `PathRemapperTests`, `RestoreTests` (end to end, mapping, refusals, locks, crash after every phase, rollback, older schema, settings merge), `BackupUiTests`.
