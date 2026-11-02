# Architecture (M1–M7)

MusicFinder is a WPF desktop app built with MVVM and dependency injection. The UI never touches SQLite or tag
libraries directly; everything goes through contracts defined in the Domain project.

## Projects and dependencies

```
App ──► Application ──► Domain
 │                        ▲
 └──► Infrastructure ─────┘   (Infrastructure also uses Application for the query parser/normalizer)
```

| Project | Responsibility |
|---|---|
| `MusicFinder.Domain` | Entities (`Track`, `Folder`, `LibraryRoot`), enums, contracts (`ISearchService`, `IIndexer`, `IMetadataReader`, repositories, `ISettingsStore`) and the models they exchange (`SearchQuery`, `SearchPage`, `ScanProgress`, `AppSettings`). No infrastructure dependencies. |
| `MusicFinder.Application` | Pure logic: `SearchQueryParser` (lexer/parser for the search grammar) and `TextNormalizer` (case, diacritic and final-sigma folding). |
| `MusicFinder.Infrastructure` | SQLite persistence (connection factory, serialized write queue, migration runner, Dapper repositories), FTS query building, SQL generation, the `LibraryScanner`, the TagLib reader and the JSON settings store. |
| `MusicFinder.App` | WPF views, view models, converters, theme dictionaries and the DI composition root (`App.xaml.cs`). |

## M2 additions

- **Artwork is split by platform.** `ArtworkExtractor` (Infrastructure, TagLib, no WPF) returns raw bytes and a hash; `ArtworkService`
  (App) decodes, resizes, freezes and caches bitmaps with WPF, so Infrastructure stays free of WPF. `ArtworkThumbnail` cancels and
  clears itself whenever a recycled grid row gets a new track.
- **Genres and tags** are services over the contracts in Domain: `IGenreService` (taxonomy edits with validation, aliases, user
  assignments), `IDjTagService`, and `GenreMappingJob` (stored text → taxonomy, no file reads).
- **Tag writing** is `IMetadataWriter` (verified temporary-copy-then-replace, see `docs/tags-and-genres.md`) behind
  `ITagEditingService` (preview diff, per-file apply, database refresh). View models only talk to the interfaces.
- **UI threading rule:** services raise events from whatever thread finished the work; view models marshal to the UI thread before
  touching observable collections (`SearchViewModel.OnUi`, `TagsViewModel.OnUi`).

## Runtime flow

1. `App.OnStartup` resolves data paths (installed: `%LOCALAPPDATA%\MusicFinder`; portable: `Data\` beside the exe),
   configures Serilog, builds the host and **runs migrations** (which also verifies that FTS5 is available).
2. `MainWindow` and the view models are resolved; `MainViewModel.InitializeAsync` loads the folder tree, genre
   options and the first page of results; then all enabled roots are rescanned in the background.
3. `ScanCoordinator` runs one scan at a time and raises progress/finished events on the UI thread.
   `LibraryScanner` reads tags with bounded concurrency and writes through the single `SqliteWriteQueue`.
4. `SearchViewModel` debounces typing (120 ms), cancels the previous query (latest wins), runs SQLite work on the
   thread pool, loads pages of 200 rows by keyset pagination, and counts results in a separate query.

## M3 additions (DJ sessions and drag-out)

- **Play inference is pure logic on an injected clock.** `PlayInferenceEngine` (Application) holds one window per export and decides
  elapsed vs interrupted from `TimeProvider`'s monotonic time, so tests advance a fake clock instead of sleeping. `DjSessionService`
  writes the append-only `TrackEvents` log, the `PlayHistory` rows and raises `PlayStateChanged` / `SessionChanged` (on any thread; view models marshal to the UI).
- **The OS drag is behind `IFileDragSource`** (App). `WindowsFileDragSource` is the only code that calls `DoDragDrop`; tests substitute a fake and the
  real thing is checked by hand (see `docs/dj-session.md`). `SearchViewModel.DragOut` plans the payload (`DragPayloadPlanner`: skip missing files, cap, dedupe),
  runs the drag, then records it through `ISessionService`.
- **Activity feeds the heuristic.** Search text, filters, sort, folder scope and a different selection call `ISessionService.NotifyActivity`.
- Search joins the trigger-maintained `TrackPlayStats` row, so the Last Played / Plays columns, the `played:` filters and the play sorts need no aggregation per query.

## M7 additions (smart Greek search, Find Similar)

- **Pure logic in Application** (`Search`, `Similar`): `GreekKeyboardLayout` (the wrong-layout reading, verified against the Windows Greek layout), `GreekPhoneticKey`, `QueryExpander`
  (a typed word becomes a bounded set of forms: as typed, normalized, other layout, phonetic key, typo candidates), `SearchTextBuilder`, `SearchAliasIndexJob`, `SearchAliasService`,
  `CamelotKey`, `GenreRelations`, `SimilarityScorer`, `SimilarTracksService`. None of it touches SQLite or WPF.
- **Infrastructure** turns the forms into FTS expressions (`FtsMatchBuilder`: every value is a quoted literal in a parameter, column names come from fixed tables), builds the SQL
  (`SearchSqlBuilder`: tiers, relevance key, two-step page read) and reads the typo vocabulary (`FtsVocabulary`). `SqliteSearchService` also implements `ISearchDiagnostics`
  ("Explain a search").
- **App**: `SearchIndexCoordinator` keeps the alias index current in the background and re-runs an on-screen query when indexing finishes; `SearchViewModel` has the relevance default and the
  Find Similar mode; Settings has the Search panel.
- Two search engines coexist on purpose: the original index and the alias index. A query is answered from the alias index, and from the original one only while some tracks are not indexed yet.

## M5 additions (online identification)

- **Domain** (`Contracts/Identification.cs`, `Models/IdentificationModels.cs`): `IIdentificationService`, `IFingerprintService`, `IAcoustIdClient`, `IMusicBrainzClient`, `IIdentificationCache`, and the candidate / outcome / readiness models.
- **Application** (`Identification`): `IdentificationService` (opt-in gate, method choice, caching, candidates; it only proposes), `IdentificationProposalBuilder` (per-field proposals; nothing is ticked that would replace a value),
  `CandidateEvidence`, `MusicBrainzQueryBuilder` (Lucene phrases, only `\` and `"` escaped inside quotes), `RateLimiter` and `RetryPolicy` (pure, `TimeProvider`-driven), `UserAgentBuilder`.
- **Infrastructure** (`Identification`): `IdentificationHttp` (retry -> rate limit -> network pipeline, hand-built, no extra package), `MusicBrainzClient` + `MusicBrainzJson`, `AcoustIdClient` (gzip POST),
  `ChromaprintFingerprintService` + `FpcalcLocator` + `WindowsProcessRunner` (no shell, `ArgumentList`, killed on cancel or timeout), `IdentificationCacheRepository`.
- **App**: `IdentificationConsentViewModel` / window (what is sent, contact, key), `IdentificationSettingsViewModel` (Settings section), `IdentifyViewModel` / `IdentifyWindow` (queue, candidates, release, proposals, preview, write).
  Writing goes only through `ITagEditingService`; `TagField.MusicBrainzRecordingId` and a front cover (`TagChangeSet.Cover`, fetched by `CoverArtClient`, checked by `CoverImageReader`) are the new writable things.
- Tests replace the network (`FakeHttpHandler`), the process (`FakeRunner`) and the whole service (`FakeIdentification`); nothing in the default suite contacts a real service.

## BPM and key analysis

- **Domain**: `Contracts/AudioAnalysis.cs` (`IAudioDecoder`, `IAudioAnalysisService`, `ITrackAnalysisStore`), `Models/AudioAnalysisModels.cs`.
- **Application/Analysis** (pure, no I/O, tested on float arrays): `Fft`, `SilenceGate`, `OnsetEnvelope` + `TempoEstimator`, `Chromagram` + `KeyEstimator`, `AudioAnalyzer` (version-stamped),
  `AudioAnalysisService` (decode, analyse, remember), `AnalysisRunner` (dedicated below-normal-priority threads, 1-2 at once), `AnalysisProposalBuilder` (the ticking rules); `CamelotKey` gained the formatters.
- **Infrastructure**: `Audio/AudioDecoders.cs` (`MediaFoundationAudioDecoder`, `WaveAiffAudioDecoder`, `VorbisAudioDecoder`, `AudioDecoderRouter`), `TrackAnalysisRepository`, migration 0008.
- **App**: `AnalyzeViewModel` / `AnalyzeWindow`, `AnalysisSettingsViewModel`; entry points in `LibraryViewModel` (`AnalyzeCommand`, `AnalyzeMissingCommand`). Writing goes only through `ITagEditingService`.

## Library Health (M6)

- **Domain**: `Enums/IssueFlags.cs`, `Models/HealthModels.cs` (`HealthIssueKind`, `HealthSummary`), `IMetadataHealthService` (`GetSummaryAsync`, `QueryIssuesAsync`).
- **Application/Health**: `MojibakeDetector`, `QualityRules`, `HealthIssues` (titles, explanations and the Library search for each finding).
- **Infrastructure**: `Health/MetadataHealthService` (SQL counts, no file read), reader fields in `TagLibMetadataReader`, `ReadError` and unsupported-file counting in `LibraryScanner`, migration 0009.
- **App**: `HealthViewModel` / `HealthView` (the Health tab), `LibraryViewModel.ShowFiltered` (a card opens the Library on its search).

## Threading and data rules

- All writes go through one serialized queue, each in its own short transaction. Reads use separate pooled connections (WAL mode).
- No disk scan or SQL runs on the UI thread. Long operations accept `CancellationToken`.
- Timestamps are UTC ISO-8601 text. Original path casing is preserved; comparisons are case-insensitive on Windows.
- MusicFinder never writes to, renames, moves or deletes music files. Tracks that disappear are flagged `Missing`;
  an unreachable root is marked `Offline` and its tracks are left untouched.

## Testing

| Suite | What it covers |
|---|---|
| `MusicFinder.UnitTests` | Parser, FTS expression builder and escaping, text normalization, genre normalization, starter-taxonomy validity. |
| `MusicFinder.IntegrationTests` | Migrations (including upgrade from an M1 database), FTS triggers, repositories, keyset pagination, taxonomy-aware search, genre mapping, scanner behaviour, artwork extraction, the tag writer on generated MP3/FLAC/WAV files, and the full edit path (file, database, search index, rescan). |
| `MusicFinder.UiSmokeTests` | The real `MainWindow` with the real composition root on a temp database: rows, search, selection, tabs, layout persistence, filters, tag/genre pickers, artwork service and control (real WPF decoding), the tag editor, the Tags tab, and a binding audit (open popups included) that fails on any broken binding. Runs serially because all tests share one WPF dispatcher. |

Not covered by automated tests: the real OS drag loop and interoperability with VirtualDJ/Traktor (see `docs/dj-session.md` for what was checked by hand), real-hardware rendering, large-library performance.
