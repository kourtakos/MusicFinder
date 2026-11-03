# Changelog

## 0.9.0 (first packaged build)

Everything is offline-first; nothing is sent anywhere unless online identification is turned on.

- **Search**: instant token/prefix search over indexed folders, folder tree, genre/BPM/decade/DJ-tag filters, advanced syntax, Greek accents, Greeklish and wrong-keyboard-layout matching, Quick Search pop-up.
- **Library**: embedded covers with a thumbnail cache, genre taxonomy and aliases, DJ tags, tag editor with preview and safe write, offline BPM and key estimation.
- **DJ session**: drag files out to VirtualDJ/Traktor, 20-second heuristic (possible, never confirmed), manual mark played/not played.
- **Identification** (opt-in): MusicBrainz lookup and AcoustID/fpcalc fingerprints with a field-by-field diff.
- **Organize**: batch rename and move with full preview, journal, crash recovery and undo; exact and probable duplicate finders (no automatic deletion); Library Health dashboard; Find Similar.
- **Backup and restore**: consistent SQLite snapshot in a verified `.musicfinder-backup.zip` (no music files, no secrets by default); restore with folder remapping, safety backup and a safe swap at restart.
- **Preview player**: double-click a track to hear it in a slim strip at the top (play/pause, stop, timer, cover, scrolling details, seek, volume); the output device is chosen in Settings; a preview is never a DJ play (docs/preview-player.md).
- **Packaging**: self-contained win-x64 zip with third-party notices (`build\publish.ps1`).

Not in this build: VirtualDJ/Traktor history importers (milestone M4), code signing, a bundled `fpcalc`.
