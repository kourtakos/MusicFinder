# Database (M1)

SQLite via `Microsoft.Data.Sqlite` + Dapper. File: `musicfinder.db` in the data directory.
WAL mode, `foreign_keys=ON`, `busy_timeout=5000`, `synchronous=NORMAL`.

## Migrations

Embedded resources `Persistence/Migrations/NNNN_name.sql`, applied in order, each in one transaction, recorded in
`SchemaVersions(Version, Name, AppliedUtc)`. Never rebuild the database to change the schema; add a new migration.

`0001_Initial.sql` creates every table from the CLAUDE.md contract so later milestones only add columns or tables:
`LibraryRoots`, `Folders`, `Tracks`, `Genres`, `GenreAliases`, `TrackGenres`, `CustomTags`, `TrackCustomTags`,
`DjSessions`, `TrackEvents`, `PlayHistory`, `IdentificationCache`, `FileOperations`, plus the FTS table below.
M1 only reads and writes `LibraryRoots`, `Folders` and `Tracks`.

`0002_GenresTagsArtwork.sql` (M2) adds `Genres.SortOrder/IsBuiltIn`, `CustomTags.SortOrder`, `Tracks.GenreMapVersion`, indexes on the
genre/tag join tables and a small `AppState(Key, Value)` table (`TaxonomyVersion`, `TaxonomySeedVersion`). The starter taxonomy and DJ tags are
inserted by `GenreTaxonomySeeder` in C# so their normalized names always come from the same `TextNormalizer` used for matching; after
the first run the data belongs to the user and deleted built-ins are not resurrected.

`0003_TrackMetaVersion.sql` (M2) adds `Tracks.MetaVersion`, the metadata reader version that produced a row (see `docs/tags-and-genres.md`).

Genre mapping: `TrackGenres(TrackId, GenreId, Source)` with `Source` = `Embedded` or `User`. A track's `GenreMapVersion` is compared to
`AppState.TaxonomyVersion`; any taxonomy edit bumps the version and `GenreMappingJob` remaps stale tracks in batches without reading files.
The job stamps a track only if its raw genre is unchanged since it was read, so a concurrent rescan cannot be overwritten.

Additions over the contract: `Tracks.NormalizedText` (see Search) and indexes on `Title`, `Artist`, `Bpm`, `Year`, `Missing`, `Folders.RootId`.

`Tracks.ArtworkCacheKey` is the lowercase hex SHA-1 of the embedded front cover (null when none): the content-addressed key of the thumbnail cache.

Known gap: favorites are shown in the reference design but have no table yet; they need their own migration.

## Tracks and folders

- `Tracks.FullPath` is unique; the scanner upserts by path and skips files whose size and modified time are unchanged.
- `IndexedUtc` is stamped with the scan start for every file seen. After a scan, tracks under the root with an older
  `IndexedUtc` are flagged `Missing = 1`. Rows are never deleted by scanning.
- `Folders.DirectTrackCount` / `RecursiveTrackCount` count non-missing tracks and are recomputed after each scan.

## Search index

`TrackSearch` is an FTS5 external-content table over `Tracks` (`content='Tracks'`, `content_rowid='Id'`,
`tokenize='unicode61 remove_diacritics 2'`) with columns `FileName, Title, Artist, AlbumArtist, Album, GenreRaw, FullPath, NormalizedText`.
Triggers `Tracks_ai`, `Tracks_au`, `Tracks_ad` keep it in sync (updates issue a `'delete'` command with the old values, then an insert).
The migration ends with an FTS `'rebuild'`.

`unicode61` folds Latin diacritics only, so `NormalizedText` holds a C#-computed form (lowercase, tonos/diacritics removed,
final sigma `ς` → `σ`) of file name, title, artist, album artist, album and genre. Free-text terms match either the original
columns or `NormalizedText`, which makes Greek search accent- and case-insensitive. Greeklish and keyboard-layout variants are handled by the second index described under "Smart Greek search (M7)" below.

## Timestamps

All times are UTC. Two text forms exist, for a reason:

- M1/M2 columns (`Tracks.ModifiedUtc`, `IndexedUtc`) hold SQLite's default `yyyy-MM-dd HH:mm:ss[.fffffff]`: Dapper does not let a
  custom handler override a built-in type like `DateTime`, so `DateTime` parameters are written that way and read back with
  `Kind = Unspecified`. It round-trips and compares correctly by ticks.
- Everything newer (`LibraryRoots.LastScanUtc`, and the M3 tables: sessions, events, plays, statistics) is written explicitly with
  `DapperConfig.ToDb` as fixed-width ISO-8601 `yyyy-MM-ddTHH:mm:ss.fffffffZ`, so text order is chronological order.
- **Reading text that ends in "Z" gives local time** (Microsoft.Data.Sqlite converts it). Entities therefore normalize by `Kind`
  through `UtcTime.Normalize`; never just `SpecifyKind(..., Utc)`, which would shift the time by the machine's UTC offset.

Never use a conflict clause such as `INSERT OR REPLACE` inside a trigger: the clause on the statement that fires the trigger
(`INSERT OR IGNORE ...` in an importer) overrides it. The play statistics triggers use `DELETE` + `INSERT` for this reason.

## DJ sessions (M3)

`0004_DjSessions.sql` adds `PlayHistory.SupersededByPlayId`, `DjSessions.AutoStarted`, indexes and the `TrackPlayStats` table
(`TrackId`, `LastConfirmedUtc`, `LastPossibleUtc`, `LastPlayedUtc`, `LastStatus`, `ConfirmedCount`, `PossibleCount`).
Triggers `PlayHistory_ai/_au/_au_moved/_ad` recompute one track's row from its non-superseded plays; a track with no plays has no row
(so "never played" is simply a missing row). Rules learned the hard way:

- The statistics row is replaced with `DELETE` + `INSERT`, never `INSERT OR REPLACE`: a conflict clause on the statement that fires a trigger
  (an importer's `INSERT OR IGNORE`) overrides conflict clauses inside the trigger body.
- Repositories use `ON CONFLICT DO NOTHING` for the `(Source, ExternalEventKey)` de-duplication, not `OR IGNORE`, so a CHECK violation is not swallowed.
- Only `Possible` and `Confirmed` rows exist in `PlayHistory`. "Exported" and "interrupted" live only in `TrackEvents`.

## Smart Greek search (M7)

`0005_SearchAliases.sql`: `Tracks.SearchAliasVersion` (0 = the alias row is missing or stale, otherwise the `SearchTextBuilder.CurrentVersion` it was built with), the table
`TrackSearchAliases(TrackId, TitleN … FileN, TitleK … FileK, Extra)` and an external-content FTS5 table `TrackAliasSearch` over it (`unicode61 remove_diacritics 0`, because the
text is normalized in C#; `prefix='2 3'`). N columns hold normalized words, K columns the phonetic key of the same words, `Extra` the user's own aliases (both forms).
`SearchAliasOverrides(Source, Alias, SourceNormalized, AliasNormalized)` stores those aliases, `UNIQUE` per pair. `TrackAliasVocab` is an `fts5vocab` view used for typo candidates.
The original index (`TrackSearch`) is untouched: it answers exact matches and finds tracks while their alias rows are being built.

- Triggers `TrackSearchAliases_ai/_ad/_au` keep the FTS table in step (DELETE + INSERT, no conflict clauses, as in 0001 and 0004).
- `Tracks_bd_aliases` (BEFORE DELETE on `Tracks`) deletes the alias row first, so the FTS `'delete'` command always sees the old values; relying on the foreign-key cascade is not enough.
- `Tracks_au_aliasStale` sets `SearchAliasVersion = 0` when Title, Artist, AlbumArtist, Album, GenreRaw, FileName or FullPath change, whoever wrote the row. Stamping the version itself does not
  list those columns, so it does not fire the trigger. `TrackRepository` also resets the version explicitly on upsert and tag edits.
- `SearchAliasIndexJob` fills the table in batches of 500 from stored text only (no file reads). A row is stamped only if the text is unchanged since it was read (`IS` comparisons, NULL-safe),
  so a concurrent edit cannot be overwritten; one run at a time. It runs at start-up, every three seconds while a scan is adding tracks, when a scan finishes and after tag edits.
  Changing `SearchTextBuilder.CurrentVersion` rebuilds everything on the next start (the mechanism of `MetaVersion`).
- Adding or removing an alias marks only the tracks it applies to as stale (found through the original index for adds, `Extra` for removals) and runs the job.

`0006_TitleSortIndex.sql`: an expression index `IX_Tracks_TitleSort` on `(COALESCE(NULLIF(TRIM(Title), ''), FileName) COLLATE NOCASE, Id)`, exactly the title sort key. With the unary-plus
trick on `Missing` in page queries and a row-value keyset comparison (ascending), browsing by title walks this index and stops at the page size.

Query side: `SearchSqlBuilder` returns only `(Id, SortKey)` for the page, then the full rows of those ids are read (late materialization). Relevance sort key =
`printf('%d', tier * 2 + notPrimary) || char(31) || title`, still one non-null text key, so keyset cursors work as for the other text sorts.

## Online identification (M5)

`0007_Identification.sql` drops and recreates `IdentificationCache` (the 0001 table was never written by any version, so it is empty everywhere) and adds two tables:

- `IdentificationCache(TrackId PK -> Tracks ON DELETE CASCADE, FileSize, ModifiedUtc, Fingerprint, DurationSeconds, Algorithm, LengthSeconds, ComputedUtc)`: one audio fingerprint per file. It is used only while the file's size and
  modified time are unchanged, and only for the same fingerprint length.
- `IdentificationLookups(RequestKey PK, TrackId -> Tracks ON DELETE SET NULL, Method, Outcome 'Found'|'NoMatch', ResultJson, CheckedUtc, ExpiresUtc)`: one row per question asked of an online service, keyed by the question
  (SHA-1 of "acoustid|fingerprint|duration" or "mbsearch|query"), so two copies of one song share it. Found answers last 30 days, "no match" 7 days; errors are never stored.
- `MusicBrainzCache(EntityType 'recording'|'release', Mbid, Json, FetchedUtc, ExpiresUtc)`: MusicBrainz records as received, 30 days.

Times are written with `DapperConfig.ToDb` (fixed-width ISO), so "not yet expired" is a plain text comparison. `IdentificationCacheRepository` also counts and clears all three tables ("Forget remembered answers").

## BPM and key analysis (M5b)

`0008_TrackAnalysis.sql` adds `TrackAnalysis(TrackId PK -> Tracks ON DELETE CASCADE, FileSize, ModifiedUtc, AnalyzerVersion, BpmStatus/KeyStatus 'Estimated'|'NoEstimate', BpmEstimate, BpmScore, BpmLevel, BpmFlags, BpmAlternativesJson, KeyEstimate (Camelot),
KeyScore, KeyLevel, KeyRunner, KeyFlags, TuningCents, AnalyzedSeconds, Decoder, Message, AnalyzedUtc)`. Estimates are kept **apart from `Tracks.Bpm` and `Tracks.MusicalKey`** (the tags): Find Similar and the grid read only
the tags, so an estimate never looks like a tag value until the user writes it. A row is used only while the file's size and modified time and `AudioAnalyzer.Version` are unchanged; after a tag write the row is moved to
the file's new size and time (the audio did not change). "No estimate" is stored too (a silent file stays silent); decode failures are not.

## Quality and health (M6, migration 0009)

`0009_QualityAndHealth.sql` adds to `Tracks`: `Codec`, `Channels`, `BitDepth` (container facts from TagLib; NULL = unknown), `ReadError` (why the tags could not be read; NULL = read fine) and `IssueFlags` (bit mask, see `Domain/Enums/IssueFlags.cs`, 0 = nothing suspect),
with a partial index on non-zero flags. `Track.CurrentMetaVersion` is now 2, so every row indexed earlier is re-read once by the next scan and gains the new facts. `IssueFlags` is computed in C# by `QualityRules` whenever a row is written (scan or tag write), from
data the row already has; nothing is hashed or decoded. `ScanUnsupported(RootId, Extension, FileCount)` holds the per-root counts of audio-like files the scanner skips. Library Health counts are plain SQL over `Tracks` (`MetadataHealthService`), always current.

## Duplicates (M6, migration 0010)

`FileHashes(TrackId PK -> Tracks ON DELETE CASCADE, FileSize, ModifiedUtc, Algorithm 'SHA256', Hash, ComputedUtc)` holds a content hash only for files a duplicate scan has read (never filled by indexing). Trigger `Tracks_au_hashStale` deletes a track's row when its `FileSize` or `ModifiedUtc` changes, so a
remembered hash always describes the bytes the track has now; a rename or move keeps it. `DuplicateDismissals(Hash PK, DismissedUtc)` records "not a duplicate" decisions by content hash. Groups are not stored: they are `FileHashes` rows that share a hash among present files, so they are always current.

## File operations journal (M6, migration 0011)

The unused `FileOperations` table from 0001 is replaced. `FileOperationBatches(Id, Kind, Template, CreatedUtc)` and `FileOperations(Id, BatchId -> batch ON DELETE CASCADE, TrackId, Role 'Track'|'Sidecar', OldPath, NewPath, State, Error, CreatedUtc, CompletedUtc)`.
States: `Planned -> InProgress -> Done | Failed | Skipped`, and `Done -> UndoInProgress -> Undone`. A row is set to `InProgress`/`UndoInProgress` **before** the file is touched; startup recovery reads those rows. The `Tracks` row of a renamed file is updated (path, file name, search text) in the same transaction that marks its operation `Done`, so history, genres, tags and analysis (keyed by `Tracks.Id`) follow the file.
