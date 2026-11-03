# Developer handoff (resume on another PC)

Read this first on a fresh machine or in a fresh Claude Code session. `CLAUDE.md` is the contract; this file holds what is not derivable from the code.

## Set up

1. Install Git, the **.NET SDK 9.0.x** (`global.json` pins the band; .NET 10 upgrade = set `NetVersion` in `Directory.Build.props` to `10.0` and bump `Microsoft.*` in `Directory.Packages.props`), and Windows 10/11 x64.
2. `git clone <repo>`; `dotnet build`; `dotnet test` (expect all green (about 963 unit, 607 integration, 257 UI smoke; the RealRegisterHotKey UI test needs a free Ctrl+Alt+Space and an interactive desktop); UI tests need an interactive desktop session).
3. Run: `src\MusicFinder.App\bin\Debug\net9.0-windows\MusicFinder.exe --portable --software` (`--portable` keeps data in `Data\` beside the exe; `--software` avoids GPU rendering problems).
   Stop a running `MusicFinder.exe` before building (it locks the exe).
4. Self-contained test package: `dotnet publish src\MusicFinder.App\MusicFinder.App.csproj -c Release -r win-x64 --self-contained true -o dist\MusicFinder-win-x64`, add an empty `portable.flag`, zip.

## Status

M1 search foundation, M2 metadata/artwork/genres, M3 DJ session + drag-out, Quick Search overlay (global hotkey, configurable), M7 smart Greek search + Find Similar, M5 online identification (fake-service tested; real AcoustID key + fpcalc run still to do, see `docs/external-integrations.md`): **done**.
M6 slices 1 (Library Health tab) and 2 (exact duplicates, `docs/duplicates.md`) are done; slice 1 detail: (Library Health tab, quality facts and filters, see `docs/library-health.md`) is done; the rest of M6 (duplicates, rename/move with journal and undo) follows `docs/m6-plan.md`. Remaining: M4 history importers last**. BPM/key audio analysis: done (see `docs/audio-analysis.md`; key accuracy is only fair)
(needs real Traktor/VirtualDJ installs and real history samples; assume no file formats). Traktor/VirtualDJ drag interop is unverified: see `docs/traktor-test.md`.
Drag verified by hand only with Windows Media Player and Winamp (`docs/dj-session.md`).

## Working agreement

- Plan with the strongest model (a Plan agent), implement in the main session; tests first; `dotnet build` + `dotnet test` green after every step.
- Reports to the user are in **Greek**; code, docs and commits in English. State plainly what is verified and what is not. End with one concrete next step.
- Never write to the user's real music files; tag-write tests run on copies with a separate `--portable` app copy. Confirm before destructive or outward actions (deleting data, publishing, pushing).
- Never commit API keys, audio samples, generated DBs or user paths.

## Architecture rules

- Layers: Domain <- Application (pure logic) <- Infrastructure (SQLite, TagLib, scanner) <- App (WPF, MVVM, composition root `App.xaml.cs`). View models use interfaces only.
- One serialized `SqliteWriteQueue` for writes, pooled reads. Schema changes = new embedded migration `src/MusicFinder.Infrastructure/Persistence/Migrations/NNNN_name.sql` (now 0001-0012). Tests use `MigrationRunner.LatestVersion`.
- Time: `TimeProvider` everywhere; store UTC ISO via `DapperConfig.ToDb`, read via `UtcTime.Normalize`.
- UI smoke tests run serially (one WPF dispatcher); integration tests run in parallel: never call the global `SqliteConnectionFactory.ReleaseAllPools()` in test cleanup, use `factory.ReleasePool()`.
- Docs per feature in `docs/` (dj-session, quick-search, greek-search, find-similar, performance, database, architecture, search-syntax).

## Gotchas already hit

- Inside `MusicFinder.App` a bare `Application` means the namespace `MusicFinder.Application`: write `System.Windows.Application`.
- The WPF SDK leaves `System.IO` out of implicit usings (added in the App csproj).
- Dapper ignores a custom `TypeHandler<DateTime>`; write times explicitly. Reading text ending in `Z` gives local time: normalize.
- A statement-level conflict clause (`INSERT OR IGNORE`) overrides conflict clauses inside trigger bodies; triggers use DELETE + INSERT.
- FTS5 external-content tables: updates need `'delete'` with the old values; test search after every write path.
- TagLib picks the format by extension: pass the mime type for temp copies. Unchanged files (size+mtime) are skipped, so reader changes need `Track.CurrentMetaVersion` bumped.
- Never skip folders by Hidden/System attribute (Windows marks customized folders System); skip only reparse points.
- A hidden WPF window counts for `OnLastWindowClose`: keep `ShutdownMode=OnMainWindowClose`. `RegisterHotKey` error 1409 = combination taken.
- PowerShell 5.1 cannot load net9 assemblies: use a throwaway `dotnet` console project for DB inspection. Large multi-file bash heredocs with Greek text can fail: write files with an editor tool.
- Real OS drag cannot be automated (OLE returns None without a button down); interop checks are manual.

## Search benchmark

`MF_BENCH=1 dotnet test tests/MusicFinder.IntegrationTests --filter "FullyQualifiedName~SearchBenchmarkTests"` (see `docs/performance.md`; broad queries on 100k tracks are 70-85 ms, above the 50 ms target).
