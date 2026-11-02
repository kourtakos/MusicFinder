# Release checklist

Package: `build\publish.ps1` produces `dist\MusicFinder-<version>-win-x64.zip` (self-contained: the .NET runtime is inside, nothing to install; ~62 MB). `dist\` is git-ignored.

## Before building
- [ ] `<Version>` in `Directory.Build.props` is the release number; `CHANGELOG.md` has its section.
- [ ] `dotnet build` and `dotnet test` are green (the script runs the tests unless `-SkipTests`).
- [ ] New or changed NuGet packages are listed in `THIRD-PARTY-NOTICES.md` (a unit test enforces this; check the licence of each new package).
- [ ] No API key, contact address, user path, audio file or database is in the repository.

## Build
```powershell
.\build\publish.ps1
```
It runs the tests, publishes `win-x64` self-contained (ReadyToRun, not trimmed, not single-file: WPF and the SQLite native library are kept as ordinary files), adds `portable.flag`, `THIRD-PARTY-NOTICES.md` and `README-FIRST.txt`, refuses to continue if any database/settings/log file is in the folder, then zips and prints the SHA-256. Use `-NoPortable` for a build that stores data in `%LOCALAPPDATA%\MusicFinder`.

## Clean machine test (needs a person; not covered by automated tests)
On a **clean Windows 10 or 11 x64 VM without the .NET SDK or runtime installed**:
- [ ] Unzip to a writable folder; `MusicFinder.exe` starts offline with no login or prompt for a runtime. (Blank window in the VM: `MusicFinder.exe --software`.)
- [ ] A `Data` folder appears beside the exe; deleting the folder removes every trace.
- [ ] Add a folder with a few dozen copied tracks: indexing finishes, search finds Greek and Latin names, covers show.
- [ ] Settings -> Back up now creates a `.musicfinder-backup.zip`; Restore it, restart, library is back.
- [ ] Windows SmartScreen / antivirus behaviour noted (an unsigned exe is expected to warn).
- [ ] Drag a track into VirtualDJ or Traktor (see `docs/traktor-test.md`); note what the program does with it.

## Signing and publishing (decisions for the owner)
- [ ] Signing: the build is **not signed**. Without a code-signing certificate, SmartScreen shows "unknown publisher".
- [ ] Choose a licence for MusicFinder's own code before giving the zip to anyone (`THIRD-PARTY-NOTICES.md` says none is chosen).
- [ ] Attach the zip and its SHA-256 to a GitHub release only after the clean-machine test passes.

## Known limits to state in release notes
- Rename/move changes file paths; Traktor and VirtualDJ key cue points and playlists by path (the app warns).
- Restore onto other drives has been tested with temp folders only; do a real USB/NAS relocation before relying on it.
- fpcalc (fingerprinting) is not bundled; AcoustID needs the user's own key.
- Search, scan and backup speed has not been benchmarked on a 100,000-track library.
