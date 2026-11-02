# MusicFinder — CLAUDE.md

> Implementation contract for Claude Code. Version 1.1, 2026-11-01 (updated requirements).
> Product: fast, standalone, offline-first Windows DJ music library organizer and finder.

## 0. Agent operating rules

- Implement the application, not a prototype or browser mockup. Prioritize a working vertical slice over broad scaffolding.
- Target Windows 10/11 x64, .NET 10 LTS, WPF, C# nullable enabled, implicit usings enabled. Check SDK availability before generating project files.
- Use MVVM and dependency injection. UI must never directly access SQLite, network services or tag libraries.
- Keep everything offline-first. Online identification is opt-in and never blocks local search.
- NEVER silently overwrite, delete, retag or relocate source music. All mutations require explicit user confirmation and preview.
- Distinguish observed facts from inferred behavior. A drag attempt is not a confirmed DJ playback.
- Use asynchronous I/O, cancellation tokens, bounded concurrency and structured logs. No synchronous disk scans on UI thread.
- Do not invent external SDK/API capabilities. Verify APIs against installed package versions and official docs before implementing adapters.
- After each milestone run `dotnet build`, `dotnet test`, and the acceptance scenarios; report what works and what remains.
- Use explicit SQL migrations; never rebuild the database to change schema without a migration.
- Never commit API keys, audio samples, personal music files, generated DBs or user paths.
- Keep source code and technical docs in English; UI localization resources should support English and Greek.

## 1. Product definition

MusicFinder combines Everything-like instant filename/tag search, MusicBrainz Picard-like metadata lookup, folder-tree navigation and DJ session awareness. It is NOT a DAW, mixer, replacement for VirtualDJ/Traktor, streaming client, or full music player. It must remain keyboard-first, responsive, portable, minimal, and usable with 100,000+ indexed tracks.

### Primary user journeys

1. Add one or more local/network music roots; initial index runs in background; tree and results become searchable incrementally.
2. Type partial artist/title/filename, optionally select a folder and genre/BPM/decade filters; see results instantly with embedded cover thumbnails.
3. Drag one or multiple music files from MusicFinder into VirtualDJ or Traktor using standard Windows file-drop semantics (CF_HDROP/FileDrop).
4. In an active DJ session, see which tracks were dragged, possibly played, or confirmed played; filter/hide recent tracks.
5. Edit metadata, identify tracks online, inspect proposed changes, then explicitly save selected fields.
6. Batch rename/move files with a dry-run preview, conflict checks, journal and undo where safe.

### Non-goals for MVP

- No cloud sync, login, streaming catalog, LLM, automatic mixing, audio output routing or controller integration.
- No claims of detecting actual playback solely from search inactivity.
- No automatic deletion of duplicates.
- No unverified live integration with proprietary VirtualDJ/Traktor internals.

## 2. UX / visual specification

Reference visual: dark, clean 3-column DJ library dashboard; treat screenshot as visual inspiration, not literal functional requirements.

- Top: app title, global search (Ctrl+F/Ctrl+K), filter chips (genre, BPM, decade, format, played status), library/session settings.
- Left (resizable/collapsible, default 240 px): indexed root folders TreeView, nested children, counts, rescan/open/exclude context menu, Include subfolders checkbox; optional compact library status panel.
- Center (virtualized, dominant): multi-select sortable result grid, embedded 48-64px cover thumbnail, title, artist, genre chips, BPM, key, duration, bitrate, last played, play count, file status; keyboard and drag support.
- Right (resizable/collapsible, default 320 px): selected track artwork, metadata, path, editable tags, recent play events, buttons for identify and edit.
- Bottom status: total results, indexed files, index progress, offline status, session status, pending actions.
- Dark default, accessible contrast, restrained purple accent; light theme optional. No animations that impede rapid navigation.
- Window minimum 1000x650; responsive panel collapse on narrow windows. Remember sizes and visible columns.
- Results: single click selects; double click opens details or configurable action; Enter opens details; Ctrl+C copies path; Ctrl+O opens Explorer; Delete NEVER deletes without explicit confirmation.
- Drag from result row must emit real file paths accepted by Windows desktop applications. Test with VirtualDJ and Traktor on actual Windows; unit tests cannot prove interoperability.
- Artwork fallback: neutral music icon; never block grid scrolling waiting for cover extraction.
- Session controls: Start/Stop Session, timeout (default 20 seconds, configurable 15-20), Hide Confirmed, Hide Possible, show play evidence.
- Filter state combines with search and selected folder, not mutually exclusive. Global Search toggle ignores folder scope.

## 3. Technology and libraries

| Concern | Choice | Notes |
|---|---|---|
| Runtime/UI | .NET 10, WPF | Native Windows, no Electron/WebView required |
| MVVM | CommunityToolkit.Mvvm | ObservableObject, RelayCommand, AsyncRelayCommand |
| DI/config/logging | Microsoft.Extensions.Hosting, DependencyInjection, Configuration.Json, Logging; Serilog.Extensions.Hosting, Serilog.Sinks.File | Use current compatible stable versions |
| DB | Microsoft.Data.Sqlite + Dapper | Explicit transactions, parameterized SQL, migrations |
| Search | SQLite FTS5 | Verify bundled SQLite FTS5 support at startup |
| Audio tags | TagLibSharp (TagLib#) | Read/write ID3/FLAC/Vorbis where supported; test encoding |
| Fingerprint | Chromaprint `fpcalc` process adapter | Optional separately bundled executable, verify distribution license |
| Online recognition | HttpClient, System.Text.Json | AcoustID and MusicBrainz public APIs, rate limiting and caching |
| Artwork | WPF BitmapImage/BitmapDecoder | Decode off UI thread, freeze images, disk thumbnail cache |
| Preview player | Optional NAudio | Keep preview isolated, no dependency on audio capture |
| Tests | xUnit, FluentAssertions (check license) or built-in Assert, Microsoft.NET.Test.Sdk | Integration tests on temp folders and SQLite |
| Packaging | dotnet publish self-contained win-x64 | Single-file only if native deps and WPF verified |

Do not pin unverified package versions. Use NuGet stable compatible versions and record exact resolved versions in `Directory.Packages.props` with Central Package Management. Prefer permissive licensing; audit NuGet and native binaries before release. `fpcalc` may have different distribution/license implications than managed libraries. AcoustID needs an application API key and its service terms/rate limits must be respected. MusicBrainz identification is not guaranteed to provide BPM, Camelot key, genre or remix version.

## 4. Solution layout

```text
MusicFinder/
  CLAUDE.md
  README.md
  MusicFinder.slnx
  Directory.Build.props
  Directory.Packages.props
  .editorconfig
  .gitignore
  src/
    MusicFinder.App/
      App.xaml
      Views/{MainWindow,SettingsView,TrackDetailsView,TagEditorView,IdentifyView}.xaml
      ViewModels/{Main,FolderTree,Search,TrackDetails,Session,Settings}ViewModel.cs
      Controls/{TrackGrid,FolderTree,ArtworkThumbnail}.xaml
      Themes/{Colors,Typography,Controls}.xaml
      Resources/{Strings.en,Strings.el}.resx
    MusicFinder.Domain/
      Entities/{Track,LibraryRoot,Folder,Genre,Session,TrackEvent,PlayEvidence}.cs
      Enums/{PlayStatus,TrackEventType,MatchConfidence}.cs
      Contracts/{ITrackRepository,ISearchService,IIndexer,IArtworkService,IMetadataService,
                 IIdentificationService,IFileOperationService,ISessionService,IHistoryImporter}.cs
    MusicFinder.Application/
      Services/{SearchService,LibraryService,TagEditingService,GenreService,
                DjSessionService,PlayInferenceService,FileOperationService}.cs
      Models/{SearchQuery,SearchResult,TagDiff,FileOperationPlan,IdentificationCandidate}.cs
    MusicFinder.Infrastructure/
      Persistence/{SqliteConnectionFactory,Migrations,Repositories}/
      Indexing/{LibraryScanner,ScanQueue,FileChangeMonitor,Reconciler}/
      Search/{SearchQueryParser,FtsQueryBuilder}/
      Metadata/{TagLibMetadataReader,TagLibMetadataWriter}/
      Artwork/{ArtworkExtractor,ThumbnailCache}/
      Identification/{ChromaprintAdapter,AcoustIdClient,MusicBrainzClient,IdentificationCache}/
      Dj/{WindowsFileDragSource,VirtualDjHistoryImporter,TraktorHistoryImporter}/
      FileOperations/{FilePlanner,FileExecutor,OperationJournal}/
      Settings/{JsonSettingsStore}/
  tests/
    MusicFinder.UnitTests/
    MusicFinder.IntegrationTests/
    MusicFinder.UiSmokeTests/
  docs/{architecture,database,search-syntax,dj-session,external-integrations,release-checklist}.md
```

Dependencies: App -> Application + Infrastructure + Domain; Infrastructure -> Domain (+ Application abstractions as needed); Application -> Domain; Domain depends on no external infrastructure. Never reference App from other projects. Use DI composition root in App.

## 5. Persistence design

Database stored under `%LOCALAPPDATA%/MusicFinder/musicfinder.db` in installed mode, or `Data/musicfinder.db` beside executable in explicit portable mode. Thumbnail cache separately in `Cache/Artwork`. WAL mode, foreign keys ON, busy_timeout, short write transactions, one serialized writer queue and separate read connections. SQLite backups through SQLite backup API, not blind copy of live WAL DB. Store timestamps as UTC ISO-8601 or Unix milliseconds consistently. Preserve original filesystem path casing; normalize comparison keys separately. Paths may be UNC, long paths, non-Latin and removable/offline drives.

Required migrations and tables (names indicative; implement complete constraints/indices):

```sql
CREATE TABLE LibraryRoots (
  Id INTEGER PRIMARY KEY,
  Path TEXT NOT NULL UNIQUE,
  Enabled INTEGER NOT NULL DEFAULT 1,
  IncludeSubfolders INTEGER NOT NULL DEFAULT 1,
  LastScanUtc TEXT,
  Status TEXT NOT NULL DEFAULT 'Pending'
);
CREATE TABLE Folders (
  Id INTEGER PRIMARY KEY,
  RootId INTEGER NOT NULL REFERENCES LibraryRoots(Id),
  ParentId INTEGER REFERENCES Folders(Id),
  FullPath TEXT NOT NULL UNIQUE,
  Name TEXT NOT NULL,
  DirectTrackCount INTEGER NOT NULL DEFAULT 0,
  RecursiveTrackCount INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IX_Folders_ParentId ON Folders(ParentId);
CREATE TABLE Tracks (
  Id INTEGER PRIMARY KEY,
  FolderId INTEGER NOT NULL REFERENCES Folders(Id),
  FullPath TEXT NOT NULL UNIQUE,
  FileName TEXT NOT NULL,
  Extension TEXT NOT NULL,
  FileSize INTEGER NOT NULL,
  ModifiedUtc TEXT NOT NULL,
  IndexedUtc TEXT NOT NULL,
  Title TEXT, Artist TEXT, AlbumArtist TEXT, Album TEXT,
  GenreRaw TEXT, Year INTEGER, TrackNumber INTEGER,
  DurationMs INTEGER, BitrateKbps INTEGER, SampleRateHz INTEGER,
  Bpm REAL, MusicalKey TEXT, MusicBrainzRecordingId TEXT,
  ArtworkCacheKey TEXT, Missing INTEGER NOT NULL DEFAULT 0,
  FingerprintHash TEXT, ContentHash TEXT
);
CREATE INDEX IX_Tracks_FolderId ON Tracks(FolderId);
CREATE INDEX IX_Tracks_ModifiedUtc ON Tracks(ModifiedUtc);
CREATE TABLE Genres (
  Id INTEGER PRIMARY KEY,
  ParentId INTEGER REFERENCES Genres(Id),
  Name TEXT NOT NULL,
  NormalizedName TEXT NOT NULL UNIQUE
);
CREATE TABLE GenreAliases (
  AliasNormalized TEXT PRIMARY KEY,
  GenreId INTEGER NOT NULL REFERENCES Genres(Id)
);
CREATE TABLE TrackGenres (
  TrackId INTEGER NOT NULL REFERENCES Tracks(Id) ON DELETE CASCADE,
  GenreId INTEGER NOT NULL REFERENCES Genres(Id),
  Source TEXT NOT NULL,
  PRIMARY KEY(TrackId, GenreId, Source)
);
CREATE TABLE CustomTags (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL UNIQUE);
CREATE TABLE TrackCustomTags (
  TrackId INTEGER NOT NULL REFERENCES Tracks(Id) ON DELETE CASCADE,
  TagId INTEGER NOT NULL REFERENCES CustomTags(Id),
  PRIMARY KEY(TrackId,TagId)
);
CREATE TABLE DjSessions (
  Id INTEGER PRIMARY KEY,
  StartedUtc TEXT NOT NULL,
  EndedUtc TEXT,
  DjSoftware TEXT,
  Name TEXT
);
CREATE TABLE TrackEvents (
  Id INTEGER PRIMARY KEY,
  TrackId INTEGER REFERENCES Tracks(Id),
  SessionId INTEGER REFERENCES DjSessions(Id),
  EventType TEXT NOT NULL,
  OccurredUtc TEXT NOT NULL,
  Source TEXT NOT NULL,
  Confidence REAL,
  EvidenceJson TEXT
);
CREATE INDEX IX_TrackEvents_SessionTrackTime ON TrackEvents(SessionId,TrackId,OccurredUtc);
CREATE TABLE PlayHistory (
  Id INTEGER PRIMARY KEY,
  TrackId INTEGER NOT NULL REFERENCES Tracks(Id),
  SessionId INTEGER REFERENCES DjSessions(Id),
  PlayedUtc TEXT NOT NULL,
  Status TEXT NOT NULL CHECK(Status IN ('Possible','Confirmed')),
  Source TEXT NOT NULL,
  ExternalEventKey TEXT,
  RelatedEventId INTEGER REFERENCES TrackEvents(Id),
  UNIQUE(Source,ExternalEventKey)
);
CREATE INDEX IX_PlayHistory_TrackTime ON PlayHistory(TrackId,PlayedUtc DESC);
CREATE TABLE IdentificationCache (
  TrackId INTEGER PRIMARY KEY REFERENCES Tracks(Id),
  Fingerprint TEXT, DurationSeconds INTEGER,
  AcoustId TEXT, MusicBrainzRecordingId TEXT,
  ResponseJson TEXT, CheckedUtc TEXT
);
CREATE TABLE FileOperations (
  Id INTEGER PRIMARY KEY,
  BatchId TEXT NOT NULL, TrackId INTEGER REFERENCES Tracks(Id),
  OperationType TEXT NOT NULL,
  OldPath TEXT, NewPath TEXT,
  BeforeMetadataJson TEXT,
  State TEXT NOT NULL,
  CreatedUtc TEXT NOT NULL, CompletedUtc TEXT
);
```

FTS5: use an external-content FTS table tied to `Tracks` rowid and triggers for INSERT/UPDATE/DELETE, including initial `rebuild` after migration. Example:

```sql
CREATE VIRTUAL TABLE TrackSearch USING fts5(
  FileName, Title, Artist, AlbumArtist, Album, GenreRaw, FullPath,
  content='Tracks', content_rowid='Id',
  tokenize='unicode61 remove_diacritics 2'
);
```

CAUTION: SQLite unicode61 `remove_diacritics` primarily handles Latin diacritics; do not assume it fully normalizes Greek accents or Greeklish. Implement separately normalized searchable tokens (Greek lowercasing, tonos removal, transliteration aliases) and test with Greek/Greeklish examples. If adding `SearchAliases` or extra FTS columns, use a new migration. Ensure FTS columns match actual Tracks names and maintain triggers correctly. Avoid arbitrary user input directly in MATCH expressions.

## 6. Indexing pipeline

1. User adds root; canonicalize path, check accessibility, detect overlapping roots and duplicate files.
2. Breadth-first enumerate folders and supported files (`.mp3`, `.flac`, `.m4a`, `.aac`, `.ogg`, `.wav`, `.aiff` where metadata support is verified). Skip unsupported gracefully.
3. Queue bounded metadata reads; cap concurrency (default 2-4 disk readers, configurable); prioritize UI responsiveness.
4. Read basic filesystem stats, then tags with TagLibSharp; errors recorded per file, scan continues.
5. Upsert track by canonical path, compare size+mtime to avoid unnecessary retagging; maintain folder hierarchy and FTS transactionally.
6. Extract embedded artwork lazily on first visible result or during low-priority background work. Cache scaled 64/96px thumbnail keyed by path/mtime/art hash; invalidate on tag change.
7. `FileSystemWatcher` on local roots: debounce bursts, coalesce events, handle rename/move, overflow and missing notifications; periodic full reconciliation. NAS/removable volumes may need scheduled rescan rather than reliable watcher.
8. Never interpret temporary disconnection as deletion: mark unavailable/missing and reconcile after reconnect.
9. Exclude symlink/junction loops; avoid recursively traversing reparse points unless explicitly enabled with cycle protection.
10. Publish progress and cancel safely. Persist checkpoints where possible; scan resumable after app restart.

Performance budgets (targets, not guarantees): app cold start <2s on typical SSD; warm indexed search p95 <50ms for common queries over 100k tracks; responsive scrolling and search during indexing; bounded memory, no materializing all tracks in UI. Benchmark on Windows hardware with generated test library.

## 7. Search and filtering contract

- Search while typing, 100-150ms debounce, cancel prior query, latest query wins.
- Default token AND search across title, artist, filename, album, genre and path; prefix match supported. Empty query lists current folder tracks paginated.
- Advanced grammar: `artist:madonna`, `title:frozen`, `album:...`, `genre:disco`, `bpm:115-125`, `year:1980-1989`, `decade:80s`, `ext:flac`, `missing:title`, `played:never`, `played:session`, `played:possible`, `key:8A`. Quoted phrases and literal special characters supported. Invalid expressions show nonblocking validation and never crash.
- Build parameterized SQL from parsed AST; escape FTS syntax properly, no string concatenation of untrusted MATCH input.
- Genre facet allows multiple genres, hierarchy, aliases and multi-select OR within a facet; AND across facets. Distinguish raw embedded genre from user-defined normalized genres.
- Folder scope: selected folder direct-only or recursive; Global Search toggle. Prefer recursive CTE to resolve descendants; optimize if profiling requires closure table.
- Sort by title, artist, BPM, key, duration, file date, last confirmed played, last possible played, play count. Stable secondary sort by track ID.
- Page results via keyset pagination where practical; WPF virtualization and incremental loading; no full 100k ObservableCollection.
- Search must work with Greek and Latin tags; Greeklish matching is an explicitly tested enhancement, not a promise of perfect transliteration.
- Search indexing is independent of online metadata APIs.

## 8. Genre engine

- Embedded raw genre never discarded.
- Ship editable starter taxonomy with broad parent categories (Electronic, House, Disco, Funk, Pop, Rock, Hip-Hop, R&B, Soul, Jazz, Latin, Reggae, Classical, Greek, Folk, World, etc.) and subgenres. This is a curated expandable taxonomy, not a claim to include every genre on earth.
- Aliases/synonyms: e.g. `nu disco`, `nu-disco`; localized names and DJ-specific genres.
- Track may have multiple genres from embedded tags, normalized mapping and user assignments; track provenance for each mapping.
- Filters include Unknown Genre, Uncategorized, Recently Played, BPM range, decade and custom DJ tags (Warm Up, Peak Time, Closing, Wedding, Greek Party).
- Do not overwrite embedded genre tags when modifying internal normalized genres unless user explicitly selects sync-to-file.

## 9. DJ session and play inference — critical rules

Target DJ applications: VirtualDJ and Traktor. Implement generic OS file drag first, then optional history importers.

### Drag event state machine

- `DragStarted`: user initiates drag with selected track(s). Log attempt only.
- `DragCompletedUnknown`: WPF `DoDragDrop` returns a drop effect; this is not proof the target loaded or played it. Capture outcome when available.
- `Exported`: track appears to have been transferred; mark as inferred export, never confirmed play.
- Start a 20-second (configurable 15-20s) *heuristic window* after a drag. If no further relevant search/selection occurs, mark `Possible` play with source `InactivityHeuristic`. If activity occurs, leave as `Exported`/pending, NOT `NotPlayed`.
- The 20-second heuristic is weak evidence; never claim it proves playback. User can manually Mark Played / Not Played / Undo.
- An optional configurable stronger heuristic can consider application focus and subsequent drag activity, but must remain explicitly uncertain.
- History importer event that can be reliably matched to the local file upgrades the play to `Confirmed`; preserve original inference event, link evidence, avoid double counting.
- If same track is dragged twice, retain two events. Deduplicate confirmed history imports by stable external event ID or (source, timestamp, file identity) with documented tolerance.
- Session starts/stops manually; allow auto session suggestion on first drag, never silently merge unrelated sessions.
- Show `Last Confirmed`, `Last Possible`, `Confirmed Count`, `Possible Count`, `Played This Session`, and `Elapsed Since Last Play` separately.
- Allow Hide Confirmed in Session, Hide Possible in Session and warning when recently used. No automatic removal from library.

### VirtualDJ history adapter

- Build an adapter for actual VirtualDJ history files, discovered from user-selected paths or documented defaults after verifying installed version. Inspect sample files and implement parser with fixtures; do not assume schema or live-update behavior.
- VirtualDJ `historyDelay` can influence what counts as played. Read-only integration; do not modify user DJ settings without explicit request.
- Match using canonical path where possible, then stable metadata/size heuristics with user review for ambiguous matches.
- Incremental import, idempotent on restart, time zone handling, and no duplication.

### Traktor history adapter

- Support documented history exports/archives or collection data only after verifying actual format/version. Do not assume Traktor exposes live file updates or an official playback API.
- If reliable history is available only on shutdown/export, label status `Sync after export/close` and never promise real-time confirmation.
- Keep adapter behind `IHistoryImporter` and ship with test fixtures.

### Optional future audio detection

WASAPI loopback/fingerprinting is NOT MVP; ASIO or external controller routing may bypass it, and mixing/pitch changes impair accuracy. No microphone listening by default.

## 10. Metadata editor and identification

- Read Title, Artist, Album Artist, Album, Genre, Year, Track Number, BPM, Key, Duration, embedded picture, MusicBrainz identifiers.
- Single/multi-track edit; show mixed-value state for bulk edits; per-field selection; validate year/BPM; preserve unedited frames and embedded cover.
- Write only after preview/confirmation; on write failure preserve source, log error, do not corrupt DB. After successful write refresh file stats, FTS and artwork cache.
- Quick lookup: MusicBrainz recording/release search based on tags. Show candidate score and evidence; no blind auto-apply.
- Fingerprint lookup: `fpcalc` -> AcoustID -> MusicBrainz recording/release candidates. Run in background with cancel, timeouts, retry/backoff, rate limits, cache.
- Use explicit app User-Agent/contact info configured for MusicBrainz. Respect MusicBrainz service limit (generally <=1 request/sec for clients) and AcoustID API limits/terms; verify current official rules before shipping.
- Correctly distinguish recording vs release metadata; album/year can vary across releases. Let user choose release.
- Show side-by-side diff per field; allow choose individual fields, optional embedded cover lookup only where lawful/available and user requested. Avoid assuming MusicBrainz supplies artwork directly.
- If metadata ambiguous, never silently replace.

## 11. Artwork

- Embedded art first (ID3 APIC / format equivalent), then local folder art optionally (`cover.jpg`, `folder.jpg`) if user enables it; otherwise fallback icon.
- No online cover downloading in core MVP.
- Decode and resize off UI thread, cache thumbnails on disk, maintain small bounded in-memory LRU; freeze WPF bitmaps for cross-thread use.
- Never store full-size images in SQLite or load hundreds of covers eagerly. Use lazy viewport-based loading and cancel work for recycled rows.
- Invalidate cache on file/tag change, prevent stale covers after rename, handle malformed artwork safely.

## 12. Batch file operations and duplicates

- Rename template variables: `{Artist}`, `{Title}`, `{Album}`, `{Year}`, `{Track:00}`, `{Extension}`. Sanitize invalid Windows characters, reserved device names, trailing periods/spaces, path lengths and missing tags.
- Move templates: `{Genre}/{Artist}/{Album}/{Artist} - {Title}.{Extension}` with explicit preview of source and destination.
- Detect case-only rename, same-file moves, cross-volume moves, collisions, existing files, locked files, read-only paths, and duplicate destinations.
- Journal before execution; atomic rename when possible; for cross-volume move use copy/verify/then-delete only with explicit approval and safe rollback strategy.
- Undo rename/move only if path identity and current state permit; do not promise universal undo for tag writes. For tag writes offer explicit backup option and per-field before-values.
- Exact duplicates: group by size, then hash content in background (SHA-256 or comparable strong hash), no delete in MVP. Similar songs/remixes are NOT exact duplicates; optional metadata-based possible duplicates shown separately.

## 12A. Find Similar Tracks (NEW, required)

- Add context-menu action `Find Similar` on a selected track, and a non-blocking Similar Tracks panel using the existing results grid.
- Deterministic, offline-first scoring, no paid AI or cloud embeddings required. Use only observed fields; do not fabricate BPM, key, energy or similarity.
- Candidate generation: same or related normalized genre, BPM window (default +/- 8 BPM), harmonic compatibility (Camelot or semitone-based rules if validated), matching custom DJ tags, artist/era as optional secondary signals.
- BPM comparisons may consider half/double tempo (e.g. 70 vs 140) but mark this explicitly; user can toggle.
- Score on a 0-100 scale with configurable weights (suggested: genre 35, BPM 30, key 25, DJ tags 10). Re-normalize weights when a field is absent; show `insufficient data` when too few signals exist, rather than presenting a misleading confidence score.
- `Similar` means metadata/DJ compatibility, NOT acoustic perceptual similarity. No claims about track energy unless computed by an independently validated analyzer.
- Exclude exact same file by default; optionally exclude recently confirmed/possibly played tracks. Show why each result matched (`BPM +3`, `same genre`, `compatible key`).
- Implement `ISimilarTracksService.FindAsync(trackId, options, cancellationToken)` and unit-test ranking, missing values, half/double tempo and stable tie-breaks.

## 12B. Duplicate and Audio Quality Inspector (NEW, required)

- Distinguish **Exact File Duplicate** (identical SHA-256), **Probable Same Recording** (matching normalized artist/title/duration and optionally AcoustID), and **Different Version/Remix** (similar title but remix/edit indicators). Never auto-merge these categories.
- Exact duplicate pipeline: file-size buckets -> streaming SHA-256 on same-size candidates; cache hash by file identity/size/mtime and invalidate when changed. Hashing is cancellable, low priority, I/O throttled.
- Probable duplicate matching uses canonicalized artist/title, duration tolerance, featured-artist parsing and explicit version tokens (`radio edit`, `extended`, `live`, `remix`, `instrumental`). False positives must be reviewable.
- Quality columns: format/container, codec where available, bitrate (including VBR display), sample rate, channels, bit depth if supported, duration, file size, tag completeness and decoder-readability status.
- Flag suspect metadata/codec mismatch, unusually low bitrate, unreadable/truncated files, and suspicious sample rate; never assert an MP3 is 'fake 320 kbps' from bitrate alone. Optional future spectral analysis must be labeled heuristic and not treated as proof.
- Report duplicate groups and quality issues with sortable filters, review preview and safe `Open in Explorer`; **no automatic deletion**. Do not hash every file during initial indexing.
- Define `IDuplicateScanService`, `IAudioQualityService` and persisted scan checkpoints/results. Tests must cover same content/different name, near-identical metadata/different audio, remix variants, unreadable files and cancellation.

## 12C. Backup, Restore and Relocation (NEW, required)

- Add `Backup Library` / `Restore Library` actions. Backup includes a consistent SQLite snapshot via SQLite Online Backup API (or `VACUUM INTO` when appropriate), app settings, genre taxonomy/aliases, DJ custom tags, session/play history, identification cache and a versioned manifest. Artwork cache is regenerable and optional.
- Do not copy a live WAL-mode SQLite database as a lone `.db` file. Use consistent snapshotting, checksum each payload and verify archive integrity before reporting success.
- Use versioned ZIP archive (`.musicfinder-backup.zip`) with manifest schema version, creation UTC, app version, root mappings, file hashes and optional archive encryption only if correctly implemented and tested; do not invent security guarantees.
- Restore preview shows data categories, existing DB impact and indexed root mappings. Allow remapping `D:\DJ Music` -> `E:\DJ Music` or UNC shares; retain stable track identity when feasible and reconcile missing paths without deleting history.
- Before restore, create a safety backup of the current library and require confirmation; restore to a staging location, validate integrity/schema, then swap safely on restart or via a controlled exclusive connection.
- Backup stores metadata/index/history, **not the music files themselves**. State this prominently in UI and README. Support manual backup first; scheduled backup is optional after MVP.
- Implement `ILibraryBackupService` with integration tests for WAL snapshot, damaged archive, schema mismatch, path relocation and rollback.

## 12D. Metadata Health Dashboard (NEW, required)

- Add `Library Health` view with counts and actionable filters for missing artist/title/album/genre/year/artwork/BPM, unreadable tags, suspicious mojibake, duplicate candidates, unsupported formats and codec errors.
- Never infer missing BPM from MusicBrainz; MusicBrainz does not reliably supply it. Offer editable manual BPM and optional future audio analysis.
- Distinguish `missing`, `unknown`, `not supported`, `not scanned`, and `error` rather than collapsing all into empty fields.
- Provide multi-select actions: edit tags, MusicBrainz lookup, batch rename preview, rescan, inspect file; show per-track diff and require confirmation for writes.
- Health summary is derived from persisted metadata and incremental updates; do not rescan all media on each dashboard opening. Expose `IMetadataHealthService.GetSummaryAsync` and `QueryIssuesAsync`.
- Tests: empty strings vs whitespace, corrupted tags, missing embedded art, invalid UTF-8/mojibake heuristics, mixed format support and post-edit count refresh.

## 12E. Smart Greek Search / Greeklish / Wrong Keyboard Layout (NEW, P0)

**Core requirement:** Search must be automatic, enabled by default, and bidirectional. A user typing Latin/Greeklish or using the wrong physical keyboard layout must find Greek-tagged files; Greek queries must also find Greeklish/Latin-tagged files. Never require a mode switch.

Three separate strategies:
1. Unicode normalization: invariant case folding, Greek final sigma equivalence (`ς`/`σ`), Greek tonos/dialytika handling, whitespace and punctuation normalization. Preserve original values for display and file writes.
2. Physical keyboard mapping: reversible key-position translation for standard Greek/US QWERTY including punctuation and semicolon/tonos behavior. This is NOT transliteration. E.g. `kalhmera` is Greeklish; a mistyped keyboard-layout sequence must be mapped by physical keys. Test realistic actual key mappings instead of assuming arbitrary examples are correct.
3. Greeklish transliteration: deterministic token-level variants, e.g. `xaris`/`haris` <-> `χαρης`/`χάρης`, `alexiou`/`aleksiou` <-> `αλεξίου`, `papazoglou` <-> `παπάζογλου`, and digraphs `th`, `ch`, `ps`, `ou`, `mp`, `nt`, `gk`, `ai`, `ei`, `oi`, `y`, `i`. Greeklish is ambiguous: limit variant explosion, prioritize likely alternatives and allow curated aliases/artist overrides.

- During indexing, populate a separate `TrackSearchAliases` / FTS5 index with normalized original tokens, canonical Greek-to-Latin forms and selected alternatives; preserve original FTS5 index for exact matches. Use explicit migrations, stable track IDs, and transactional update/delete triggers or managed reindex operations.
- Query parser produces bounded alternatives (max configurable, default 12 per token, max 64 total variants); combine using safe parameterized FTS MATCH expressions and never interpolate raw query text into SQL syntax.
- Ranking: exact field match > case/diacritics-normalized > corrected keyboard layout > transliteration > bounded fuzzy typo candidate. Title/artist rank higher than path; use deterministic secondary sort. Prevent expansion drowning out exact English-language matches.
- Prefix search must work for Greek and Latin tokens. Mixed input (`xaris Αλεξίου`) and paths containing Greek/Latin are supported. `artist:` and other scoped filters use the same normalization pipeline.
- Fuzzy matching is optional and runs only on a limited candidate set; never full-table edit-distance per keystroke. Measure latency on 100k+ synthetic entries.
- Add a diagnostics/debug test utility that shows parsed tokens, variants and match provenance; not required in everyday UI.
- Required regression fixtures: `Παπάζογλου` <-> `papazoglou`, `Χάρις Αλεξίου` <-> `xaris alexiou`, `Αλεξίου` <-> `aleksiou`, `Μαζωνάκης` <-> `mazonakis`, `Γιώργος` <-> `giorgos`/`yorgos`, Greek final sigma, accents, mixed scripts, literal Latin-only songs, wrong keyboard layout both directions, FTS punctuation and quoted phrases.
- Explicitly document that automatic Greeklish transliteration is heuristic; no perfect one-to-one mapping exists. Provide user-editable search aliases for edge cases.

## 13. Settings and deployment

Settings: indexed roots/exclusions, watcher/reconcile interval, supported formats, genre aliases, artwork cache limit, theme/language, search syntax, session heuristic 15-20 seconds (default 20), DJ software history paths, AcoustID API key, privacy controls, logs.

Portable mode uses writable `Data/` directory; installed mode uses LocalAppData. Do not assume Program Files is writable. User may choose DB/cache location. All data local; only identification requests contact external services. Explain exactly what metadata/fingerprint is sent. No telemetry by default.

Publish `win-x64` self-contained, test on clean Windows VM. Sign binaries if distributing. Ensure license notices for third-party packages and native executables. Handle SQLite native dependency and fpcalc presence cleanly. Offer app-local backup/export of settings, DB and custom tags.

## 14. Interfaces / application contracts

```csharp
public interface ISearchService
{
    Task<SearchPage> SearchAsync(SearchQuery query, CancellationToken ct);
}
public interface IIndexer
{
    Task ScanRootAsync(long rootId, IProgress<ScanProgress> progress, CancellationToken ct);
    Task ReconcileAsync(long rootId, CancellationToken ct);
}
public interface IMetadataService
{
    Task<TrackMetadata> ReadAsync(string path, CancellationToken ct);
    Task<TagWriteResult> WriteAsync(string path, TagChangeSet changes, CancellationToken ct);
}
public interface IIdentificationService
{
    Task<IReadOnlyList<IdentificationCandidate>> IdentifyAsync(long trackId, CancellationToken ct);
}
public interface IHistoryImporter
{
    string ProviderId { get; }
    Task<HistoryImportResult> ImportAsync(HistoryImportRequest request, CancellationToken ct);
}
public interface ISessionService
{
    Task<long> StartAsync(string? djSoftware, CancellationToken ct);
    Task RecordDragAsync(long sessionId, IReadOnlyList<long> trackIds, CancellationToken ct);
    Task ConfirmPlayAsync(long sessionId, long trackId, DateTimeOffset playedAt, CancellationToken ct);
}
```

Types are contracts to implement; names may be refined with tests, but do not collapse all logic into ViewModels.

## 15. Quality / acceptance criteria

### Search and index

- Index a fixture with >=100k synthetic entries without UI freeze or excessive memory; common warm searches meet p95 budget on benchmark machine.
- Add/remove/rename files and directories; DB/tree/search update correctly; watcher overflow and offline NAS do not wipe library.
- Search Greek accents, bidirectional Greeklish aliases, wrong-keyboard-layout variants, punctuation, partial tokens, quoted phrases, field filters and invalid syntax.
- Folder tree lazy-loads, handles 10k+ folders, counts remain accurate, Include Subfolders and Global Search work.
- Multi-select drag sends actual file paths to external Windows target; manual compatibility test on VirtualDJ and Traktor.

### DJ session

- Drag then 20s inactivity => Possible, never Confirmed.
- New search before 20s => exported/pending, not automatically rejected.
- History match => Confirmed, no duplicate count after repeated import.
- Multiple drags and sessions tracked independently; times displayed in local time but stored UTC.
- Manual confirmation/correction updates history and filters immediately.

### Tags and safety

- TagLib read/write round-trip for supported formats, including Greek Unicode and embedded cover.
- Failed write does not leave DB claiming success; recovery/rescan possible.
- Batch rename preview catches collisions and invalid paths; no silent overwrite; undo validated.
- Duplicate scan never deletes files.
- Artwork lazy load and invalidation work; malformed images do not crash app.

### New features acceptance

- Find Similar yields stable, explainable scores, does not invent missing BPM/key, and handles unknown fields.
- Duplicate scan distinguishes byte-identical from likely same song; no deletions, no UI freeze, incremental hash cache.
- Library Health issues update after tag changes without a full scan; bulk fixes always preview and confirm.
- Backup while SQLite WAL is active restores to a valid DB; corrupt archives rejected; disk relocation mapping preserves history.
- Bidirectional Greek/Greeklish and wrong-keyboard-layout tests pass for artist, title and filename, with mixed-language queries; exact-match ranking wins over heuristic matches.

### Resilience and packaging

- App starts offline, no login; indexing/search continue without network.
- Corrupt/unreadable audio files and inaccessible folders logged and skipped.
- DB migration upgrade tested from prior version; backup/recovery instructions documented.
- Self-contained win-x64 release launches on clean Windows 10/11 test VM.

## 16. Test plan

Unit: search lexer/parser and escaping, normalization/Greeklish, genre aliases, play inference state machine, path sanitization, file-operation planner, duplicate deduplication.

Integration: SQLite FTS triggers, recursive folder queries, incremental indexing, rename events, tag reads/writes against small generated audio fixtures, migration upgrade, artwork caching, history parser fixtures, idempotent imports, cancellation and failure handling.

UI smoke: search focus/keyboard shortcuts, virtualization, selection, folder toggle, drag source, edit preview, session badges, narrow layout.

Manual on Windows: actual VirtualDJ and Traktor drop acceptance, real history format/version, UNC/NAS, removable disk, high-DPI, mixed Greek/English metadata, large library.

Use deterministic fixtures and fake HTTP responses; never call MusicBrainz/AcoustID in default unit tests. External integration tests opt-in with credentials.

## 17. Milestones and delivery gates

**M1 — Search foundation**: solution builds; WPF shell, DI, DB migrations, root picker, indexer, SQLite FTS, folder tree, result grid, search syntax, status/progress. Deliver usable offline search. Tests pass.

**M2 — Metadata/artwork/genres**: embedded covers with lazy cache, genre hierarchy/aliases, multi-filter UI, tag editor and safe write. Tests pass.

**M3 — DJ session**: Windows drag source, session start/stop, 20s heuristic, possible/confirmed indicators, manual confirmation, filtering. Test against real VirtualDJ/Traktor where available.

**M4 — History integrations**: VirtualDJ importer first, Traktor importer second; use verified real samples and document limitations. Idempotent history reconciliation. Tests pass.

**M5 — Identification**: MusicBrainz metadata search, optional Chromaprint/AcoustID, candidate selection, field-by-field diff and apply. Rate limits/caching. Tests pass.

**M6 — Organize/quality**: batch rename/move preview/journal/undo, exact/probable duplicate inspector, quality checks and Library Health dashboard. Tests pass.

**M7 — Smart discovery**: production-grade bidirectional Greeklish + keyboard-layout correction, alias indexing, `Find Similar` explainable ranking and performance benchmarks. Implement basic normalization in M1 and extend here. Tests pass.

**M8 — Backup/release**: consistent SQLite backup/restore, drive path remapping, portable packaging, clean Windows VM testing, release notes and final performance benchmarks. Tests pass.

At each milestone: update README, add tests, verify build, include concise change summary, risks and next steps. Do not mark milestone complete based on scaffolding alone.

## 18. First implementation command

Start with M1 only. Generate the solution and project references, package management, domain entities, first migration, FTS triggers, indexer, repository, WPF main window and tests. Ensure `dotnet build` and `dotnet test` succeed. Provide instructions to run on Windows. Avoid implementing network services until M1 is working.

## 19. Open technical decisions to validate before coding adapters

- Exact VirtualDJ and Traktor versions, supported history file formats and locations.
- Which audio formats are actually needed beyond MP3/FLAC and their write-support matrix.
- License/redistribution of chosen TagLibSharp package, Chromaprint/fpcalc binary and other dependencies.
- Whether full standalone search should support only indexed roots (yes by default) or one-off ad hoc paths (future).
- Whether Greeklish search is basic transliteration or user-editable aliases (start with basic + aliases).
- Whether playback inference uses 20s since drag without new search, and how new selection/drag influences state (configurable, always uncertain).

**Success definition:** A DJ can open MusicFinder, browse indexed folders, search by metadata instantly, filter genre and recent plays, see embedded covers, drag files to VirtualDJ/Traktor, and later confirm what actually played—all without losing or silently modifying their music.
