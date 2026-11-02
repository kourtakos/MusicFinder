using MusicFinder.Domain.Entities;
using MusicFinder.Domain.Enums;
using MusicFinder.Domain.Models;

namespace MusicFinder.Domain.Contracts;

public interface ISearchService
{
    Task<SearchPage> SearchAsync(SearchQuery query, CancellationToken ct);
    /// <summary>Total matches, kept separate so the first page is never delayed by counting.</summary>
    Task<int> CountAsync(SearchQuery query, CancellationToken ct);
    /// <summary>Fresh rows for specific tracks (used to refresh visible rows in place after an edit).</summary>
    Task<IReadOnlyList<SearchResultRow>> GetRowsAsync(IReadOnlyList<long> trackIds, long? sessionId, CancellationToken ct);
}

public interface IIndexer
{
    Task ScanRootAsync(long rootId, IProgress<ScanProgress>? progress, CancellationToken ct);
    Task ReconcileAsync(long rootId, CancellationToken ct);
}

/// <summary>
/// Edits the genre taxonomy. Every edit that changes how raw genres map bumps the taxonomy version and remaps
/// tracks; validation failures throw <see cref="TaxonomyException"/>.
/// </summary>
public interface IGenreService
{
    /// <summary>Raised after any change to genres, aliases or user assignments (facets should reload).</summary>
    event EventHandler? TaxonomyChanged;

    Task<IReadOnlyList<GenreNode>> GetTaxonomyAsync(CancellationToken ct = default);
    Task<IReadOnlyList<(string Alias, string GenreName)>> GetAliasesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<UnmappedGenre>> GetUnmappedAsync(CancellationToken ct = default);

    Task<long> AddGenreAsync(string name, long? parentId, CancellationToken ct = default);
    Task RenameGenreAsync(long id, string name, CancellationToken ct = default);
    Task MoveGenreAsync(long id, long? newParentId, CancellationToken ct = default);
    Task DeleteGenreAsync(long id, CancellationToken ct = default);
    /// <summary>Also used by "map this unmapped value to a genre".</summary>
    Task AddAliasAsync(string alias, long genreId, CancellationToken ct = default);
    Task RemoveAliasAsync(string alias, CancellationToken ct = default);

    /// <summary>User-provenance assignment for several tracks; never changes the embedded genre text.</summary>
    Task SetUserGenreAsync(IReadOnlyList<long> trackIds, long genreId, bool assigned, CancellationToken ct = default);
    Task<IReadOnlyList<(long TrackId, long GenreId, GenreSource Source)>> GetAssignmentsAsync(
        IReadOnlyList<long> trackIds, CancellationToken ct = default);
}

/// <summary>
/// DJ sessions and play inference (CLAUDE.md section 9). A drag is recorded as an attempt and an export; it is never proof
/// of playback. After a quiet window the export becomes a Possible play (weak evidence); activity leaves it Exported.
/// Only the user (or, from M4, a DJ application's own history) can make a play Confirmed.
/// </summary>
public interface ISessionService : IAsyncDisposable
{
    /// <summary>The live session, or null.</summary>
    DjSession? Active { get; }
    /// <summary>True when there is a manual play change that <see cref="UndoAsync"/> can revert.</summary>
    bool CanUndo { get; }
    /// <summary>The session was started, renamed or ended. May be raised from any thread.</summary>
    event EventHandler? SessionChanged;
    /// <summary>Plays or session entries of these tracks changed. May be raised from any thread.</summary>
    event EventHandler<IReadOnlyList<long>>? PlayStateChanged;

    /// <summary>Closes sessions a crash left open. Call once at start-up.</summary>
    Task InitializeAsync(CancellationToken ct);
    /// <summary>Starts a session (or returns the live one).</summary>
    Task<long> StartAsync(string? djSoftware, CancellationToken ct);
    Task RenameAsync(string? name, string? djSoftware, CancellationToken ct);
    Task StopAsync(CancellationToken ct);

    /// <summary>Records tracks as exported now, without OS drag details (CLAUDE.md section 14 shape).</summary>
    Task RecordDragAsync(long sessionId, IReadOnlyList<long> trackIds, CancellationToken ct);
    /// <summary>Records a finished drag. An effect of None (cancelled) records nothing.</summary>
    Task RecordDragAsync(DragRecord drag, CancellationToken ct);
    /// <summary>Tells the service the user did something that counts as activity (cheap; never blocks).</summary>
    void NotifyActivity(UserActivity activity);

    /// <summary>Records a confirmed play at a given time (CLAUDE.md section 14 shape).</summary>
    Task ConfirmPlayAsync(long sessionId, long trackId, DateTimeOffset playedAt, CancellationToken ct);
    Task MarkPlayedAsync(long trackId, long? anchorEventId, CancellationToken ct);
    Task MarkNotPlayedAsync(long trackId, long? anchorEventId, CancellationToken ct);
    Task<bool> UndoAsync(CancellationToken ct);

    Task<IReadOnlyList<SessionEntry>> GetEntriesAsync(long sessionId, CancellationToken ct);
    /// <summary>App exit: unfinished windows become "interrupted" (never played) and the session ends.</summary>
    Task ShutdownAsync();
}

public interface IPlayHistoryQuery
{
    Task<IReadOnlyList<PlayHistoryItem>> GetForTrackAsync(long trackId, int limit, CancellationToken ct);
}

public interface IDjTagService
{
    event EventHandler? TagsChanged;

    Task<IReadOnlyList<DjTagInfo>> GetAllAsync(CancellationToken ct = default);
    Task<long> AddAsync(string name, CancellationToken ct = default);
    Task RenameAsync(long id, string name, CancellationToken ct = default);
    Task DeleteAsync(long id, CancellationToken ct = default);
    Task AssignAsync(IReadOnlyList<long> trackIds, long tagId, CancellationToken ct = default);
    Task UnassignAsync(IReadOnlyList<long> trackIds, long tagId, CancellationToken ct = default);
    /// <summary>Tag ids per track for the given tracks (tracks without tags are absent).</summary>
    Task<IReadOnlyDictionary<long, IReadOnlySet<long>>> GetAssignmentsAsync(IReadOnlyList<long> trackIds, CancellationToken ct = default);
}

public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}

/// <summary>
/// Writes tags. Nothing is written without an explicit caller decision; each file is written through a temporary copy
/// that is verified before it replaces the original, so a failure leaves the original untouched.
/// </summary>
public interface IMetadataWriter
{
    /// <summary>Checks the values (year, BPM, track number, ...) without touching any file. Empty when valid.</summary>
    IReadOnlyList<string> Validate(TagChangeSet changes);
    Task<TagWriteResult> WriteAsync(string path, TagChangeSet changes, TagWriteOptions options, CancellationToken ct);
}

/// <summary>
/// Preview-then-apply tag editing for one or many tracks. <see cref="PreviewAsync"/> never writes;
/// <see cref="ApplyAsync"/> writes only the fields that actually differ, file by file, and keeps the database in step.
/// </summary>
public interface ITagEditingService
{
    /// <summary>Raised after tracks were rewritten, with their ids (rows, covers and facets should refresh).</summary>
    event EventHandler<IReadOnlyList<long>>? TracksChanged;

    /// <summary>
    /// The distinct current value of each field across the given tracks (null = empty). One value means every track
    /// agrees; several mean the field is mixed and the editor should say so instead of showing any one of them.
    /// </summary>
    Task<IReadOnlyDictionary<TagField, IReadOnlyList<string?>>> GetFieldValuesAsync(IReadOnlyList<long> trackIds, CancellationToken ct = default);

    Task<TagEditPlan> PreviewAsync(IReadOnlyList<long> trackIds, TagChangeSet changes, CancellationToken ct = default);

    Task<TagEditSummary> ApplyAsync(IReadOnlyList<long> trackIds, TagChangeSet changes, TagWriteOptions options,
        IProgress<TagEditProgress>? progress, CancellationToken ct);
}

/// <summary>Finds artwork bytes. Decoding and resizing live in the WPF layer; this is platform-neutral.</summary>
public interface IArtworkSource
{
    /// <summary>The front cover embedded in the audio file (first picture if none is marked front), or null.</summary>
    Task<ArtworkBlob?> GetEmbeddedAsync(string audioPath, CancellationToken ct);
    /// <summary>cover/folder .jpg/.jpeg/.png next to the audio file (case-insensitive), or null.</summary>
    Task<ArtworkBlob?> GetFolderArtAsync(string audioPath, CancellationToken ct);
}

/// <summary>Read-only tag access. Writing arrives in M2.</summary>
public interface IMetadataReader
{
    Task<TrackMetadata> ReadAsync(string path, CancellationToken ct);
}

/// <summary>The user's own search aliases: names the automatic Greek/Greeklish rules cannot bridge ("Daft Punk" and "νταφτ πανκ").</summary>
public interface ISearchAliasService
{
    /// <summary>The alias list changed (the index is being updated). May be raised from any thread.</summary>
    event EventHandler? AliasesChanged;

    Task<IReadOnlyList<SearchAliasOverride>> GetAllAsync(CancellationToken ct = default);
    /// <summary>Adds an alias and re-indexes only the tracks it applies to. Validation problems come back as a result, not an exception.</summary>
    Task<SearchAliasResult> AddAsync(string source, string alias, CancellationToken ct = default);
    Task RemoveAsync(long id, CancellationToken ct = default);
}

/// <summary>Shows how a query is interpreted and why each result matched (never needed in everyday use).</summary>
public interface ISearchDiagnostics
{
    Task<SearchExplanation> ExplainAsync(string text, CancellationToken ct = default);
}

/// <summary>
/// "Find similar": other tracks of the library that fit well next to a given one, by metadata only (genre, BPM, key, DJ tags).
/// This is metadata compatibility, not acoustic similarity; it says nothing about energy or mood.
/// </summary>
public interface ISimilarTracksService
{
    Task<SimilarTracksResult> FindAsync(long trackId, SimilarTracksOptions options, CancellationToken ct);
}

/// <summary>Library Health: counts and lists derived from the stored metadata. Never rescans the music.</summary>
public interface IMetadataHealthService
{
    Task<HealthSummary> GetSummaryAsync(CancellationToken ct);
    /// <summary>Track ids with the given finding, ordered by id. Files that are gone count only for MissingFiles.</summary>
    Task<IReadOnlyList<long>> QueryIssuesAsync(HealthIssueKind kind, int skip, int take, CancellationToken ct);
}

/// <summary>Computes the SHA-256 of a file's bytes without loading it into memory.</summary>
public interface IFileHashService
{
    /// <param name="maxBytesPerSecond">0 for no limit. Reading is paced so a scan does not saturate the disk.</param>
    /// <param name="progress">Bytes read so far.</param>
    Task<string> HashAsync(string path, long maxBytesPerSecond, IProgress<long>? progress, CancellationToken ct);
}

/// <summary>Remembered file hashes and "not a duplicate" decisions. A hash is dropped whenever the file's size or modified time changes.</summary>
public interface IFileHashStore
{
    /// <summary>Files of the library (present, not Missing) that share their size with at least one other file.</summary>
    Task<IReadOnlyList<HashCandidate>> GetCandidatesAsync(CancellationToken ct);
    Task PutAsync(long trackId, long fileSize, DateTime modifiedUtc, string hash, CancellationToken ct);
    /// <summary>Present files that share a hash with another present file, without the groups the user dismissed.</summary>
    Task<IReadOnlyList<HashedMember>> GetHashedMembersAsync(CancellationToken ct);
    Task DismissAsync(string hash, CancellationToken ct);
    Task<int> RestoreDismissedAsync(CancellationToken ct);
}

public interface IDuplicateScanService
{
    /// <summary>Hashes the files that could be duplicates (same size as another file) and have no remembered hash. Cancellable and resumable: hashes already stored are kept.</summary>
    Task<DuplicateScanSummary> ScanAsync(IProgress<DuplicateScanProgress>? progress, CancellationToken ct);
    /// <summary>The groups of byte-identical files found so far. Reads stored hashes only; opens no file.</summary>
    Task<IReadOnlyList<DuplicateGroup>> GetGroupsAsync(CancellationToken ct);
    Task DismissAsync(string hash, CancellationToken ct);
}

/// <summary>Where the probable-duplicate search reads tracks from, and where "not a duplicate" decisions are kept.</summary>
public interface ISongCandidateStore
{
    Task<IReadOnlyList<SongCandidate>> GetCandidatesAsync(CancellationToken ct);
    Task<IReadOnlySet<string>> GetDismissedKeysAsync(CancellationToken ct);
    Task DismissAsync(string key, CancellationToken ct);
}

public interface IProbableDuplicateService
{
    /// <summary>
    /// Groups of tracks that are probably the same recording, and, separately, songs that exist in more than one version.
    /// Reads library data only. Nothing is ever merged, moved or deleted.
    /// </summary>
    Task<ProbableDuplicateResult> FindAsync(CancellationToken ct);
    Task DismissAsync(string key, CancellationToken ct);
}
