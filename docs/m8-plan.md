# M8 plan: backup, restore, release

| Slice | Content | Status |
|---|---|---|
| 1 | Backup core: online-backup snapshot, zip, manifest, verification, secrets left out by default | **Done** (`docs/backup.md`) |
| 2 | Restore: safety backup, staging, schema upgrade, path remap, phased swap at startup with crash recovery | **Done** |
| 3 | UI: Settings section, restore window with mapping, restart | **Done** |
| 4 | Packaging: self-contained win-x64 publish script, third-party notices, release checklist, changelog | Next |
| 5 | Opt-in benchmarks on a generated 100k library, performance doc | |

Decisions: secrets excluded unless ticked; artwork excluded unless ticked; no archive encryption (no invented guarantees); the swap happens at restart, not in-process; `BackupDatabase` rather than `VACUUM INTO`; the search index is rebuilt once after a remap and the triggers are restored from `sqlite_master`; longest-prefix-once remap with collision refusal; publish is not single-file (WPF, native SQLite, LGPL TagLibSharp stays a separate DLL); fpcalc is not bundled.
Needs the user: a clean Windows VM test of the published zip, a real USB/NAS relocation test, the release version number and whether to sign binaries.
