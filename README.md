# MusicFinder

Fast, offline-first Windows DJ music library organizer and finder. Instant search over your indexed
music folders, a folder tree, cover art, genre/tag organisation and a safe tag editor.
See `CLAUDE.md` for the full implementation contract.

**Status: M1 (search), M2 (covers, genres, tags), M3 (DJ session and drag-out), the Quick Search pop-up, M7 (smart Greek search, Find Similar), M5 (online identification) and a BPM/key audio analysis are implemented.**
MusicFinder only changes your music files when you press *Write* in the tag editor, after a preview, and never silently.
It never renames, moves or deletes files. It makes no network connection unless you turn on **online identification** in Settings, after being shown exactly what is sent (`docs/identification.md`).

## Requirements

- Windows 10/11 x64
- .NET SDK 9 (the contract targets .NET 10; it is not installed here, so the solution builds on net9.0).
  To upgrade: install the .NET 10 SDK, set `<NetVersion>10.0</NetVersion>` in `Directory.Build.props`,
  and bump the `Microsoft.*` package versions in `Directory.Packages.props`.

## Build, test, run

```powershell
dotnet build
dotnet test
dotnet run --project src/MusicFinder.App
```

Command-line options for the app:

| Option | Effect |
|---|---|
| `--portable` | Keep the database, settings, thumbnails and logs in `Data\` beside the executable instead of `%LOCALAPPDATA%\MusicFinder` (a `portable.flag` file next to the exe does the same). |
| `--software` | Force WPF software rendering. Use it if the window opens blank on a machine or remote session with a misbehaving GPU driver. |

## Using it

1. Open **Settings** (or press `+` in the Folders panel) and add the folders that contain your music.
   Indexing runs in the background and results appear as they are found. After an upgrade, existing files are re-read once.
2. Type in the search box (`Ctrl+F` / `Ctrl+K`). Search is token-AND with prefix matching and is
   accent- and case-insensitive for Greek and Latin text, and finds Greek names typed in Greeklish or with the wrong keyboard layout (`docs/greek-search.md`).
3. Combine search with the folder selection and the Genre / BPM / Decade / Tags filters.
4. Select tracks to see cover art and details. Use the `+` buttons to give them DJ tags or your own genres (works on a whole selection).
5. `F2` (or *Edit tags…*) opens the tag editor: change fields, preview the before/after, then write. See `docs/tags-and-genres.md`.
6. The **Tags** tab manages the genre taxonomy, aliases and DJ tags, and lists genre text from your files that matches nothing yet,
   so you can map it with one click.
7. `Ctrl+C` copies the selected file paths, `Ctrl+O` shows the file in Explorer. `Delete` never deletes anything.
8. **Find similar** (right-click a track) lists tracks that fit next to it by genre, BPM, key and DJ tags, with the reason for each match (`docs/find-similar.md`).
9. Drag tracks from the grid into VirtualDJ, Traktor or any player that accepts files. The **Session** tab lists what you dragged and what MusicFinder
   infers about it ("exported", "possibly played" after a quiet period, "confirmed" only when you say so). See `docs/dj-session.md`.
10. **Identify online** (`Ctrl+I`, right-click, or the button in the details panel) looks tracks up on MusicBrainz, optionally by audio fingerprint through AcoustID, and proposes tags (and the release's cover) and shows exactly what Write will change. Many tracks at once get one list with the clear matches ticked. Off until you agree (`docs/identification.md`).
   **Analyze BPM and key** (`Ctrl+B`) listens to tracks on your computer and estimates their tempo and key, with a confidence, shown apart from your tags; only confident tempos are ticked for you, keys never, and nothing is written until you press Write (`docs/audio-analysis.md`).
11. Press **Ctrl+Alt+Space** in any program to open a small always-on-top search window; drag results into your player and move the window out of the way.
   Change the shortcut in Settings. See `docs/quick-search.md`.

### Search syntax

`artist:madonna` `title:"like a prayer"` `album:` `genre:disco` `ext:flac` `key:8A`
`bpm:115-125` `year:1980-1989` `decade:80s` `missing:title|artist|album|genre|year|bpm`

Quoted phrases are supported. Invalid expressions show a warning and never block the search.
`played:never|session|possible|confirmed` filters by play status (see `docs/dj-session.md`). Greek, Greeklish and wrong-keyboard-layout matching is automatic (`docs/greek-search.md`).

## Layout

```
src/MusicFinder.Domain          entities, enums, contracts, models
src/MusicFinder.Application     search parser, text/genre normalization, genre/tag/tag-editing services
src/MusicFinder.Infrastructure  SQLite (FTS5) persistence, scanner, TagLib reader/writer, artwork extraction, settings
src/MusicFinder.App             WPF shell, MVVM view models, artwork service, DI composition root
tests/                          unit, integration (temp folders and SQLite) and UI smoke tests
docs/                           architecture, database, search syntax, Greek search, find similar, performance, tags and genres, DJ sessions, quick search
```

## Known gaps

- Tag writing is verified for MP3, FLAC and WAV (generated fixtures) and for real MP3 copies; M4A, OGG and AIFF are untested.
- The starter genre taxonomy is a curated starting point. Real libraries contain values it does not know (for example
  "General Indie Pop" or "Eurodance"); map them in the Tags tab.
- Panel widths, window size and the folder-scope toggles are saved; visible/resized grid columns are not yet.
- UI strings are English only; the Greek resource files are not added yet.
- Search was benchmarked on 100,000 synthetic tracks (`docs/performance.md`): typical queries meet 50 ms, very broad ones (two-letter prefixes, a word on a quarter of the library) take 70–85 ms. Real-library use is tested on about 3,700 tracks only.
- Greek ↔ Greeklish matching is heuristic (`docs/greek-search.md`); names such as `Daft Punk` / `νταφτ πανκ` need an alias. Wrong-layout input understands the standard Windows Greek layout only.
- Find Similar compares metadata only (genre, BPM, key, DJ tags), never how the music sounds; tracks without BPM, key or genre cannot be compared (`docs/find-similar.md`).
- Favorites (shown in the reference design) need a database migration and are not implemented.
- Drag-out is verified with Windows Media Player and Winamp only; Traktor and VirtualDJ are untested (checklist in `docs/dj-session.md`).
- No DJ-history import yet (M4), so nothing is "confirmed" except by you. File organisation, duplicates and Library Health (M6) and backup (M8) are later milestones.
- BPM and key analysis was measured on 150 files of the author's own library: confident tempos agreed with the tags 92 % of the time, keys only about 6 in 10 even at high confidence (`docs/audio-analysis.md`). Which key notation Traktor and VirtualDJ read from a tag is not verified.
- Online identification (including covers from the Cover Art Archive) is tested with fake services only; it has not yet been run against the real MusicBrainz and AcoustID with a real key and fpcalc. `fpcalc.exe` is not bundled (its licence is not verified); you supply it. BPM and key are never looked up online (`docs/identification.md`).
