using MusicFinder.Domain.Models;
using MusicFinder.Domain.Entities;
using MusicFinder.Domain.Enums;

namespace MusicFinder.Domain.Contracts;

public interface ILibraryRootRepository
{
    Task<LibraryRoot> AddAsync(string path, CancellationToken ct = default);
    Task<IReadOnlyList<LibraryRoot>> GetAllAsync(CancellationToken ct = default);
    Task<LibraryRoot?> GetAsync(long id, CancellationToken ct = default);
    Task UpdateStatusAsync(long id, ScanStatus status, DateTime? lastScanUtc = null, CancellationToken ct = default);
    Task SetEnabledAsync(long id, bool enabled, CancellationToken ct = default);
    /// <summary>Replaces the counts of audio-like files this root holds that the scanner does not index, by extension.</summary>
    Task SetUnsupportedAsync(long rootId, IReadOnlyDictionary<string, int> countsByExtension, CancellationToken ct = default);
    /// <summary>Unsupported audio-like files across all enabled roots, by extension (largest first).</summary>
    Task<IReadOnlyList<(string Extension, int Count)>> GetUnsupportedAsync(CancellationToken ct = default);
}

public interface IFolderRepository
{
    /// <summary>Insert or update by FullPath; returns the folder id.</summary>
    Task<long> UpsertAsync(long rootId, long? parentId, string fullPath, string name, CancellationToken ct = default);
    Task<Folder?> GetAsync(long id, CancellationToken ct = default);
    /// <summary>Children of a folder, or the top-level folder(s) of a root when parentId is null.</summary>
    Task<IReadOnlyList<Folder>> GetChildrenAsync(long rootId, long? parentId, CancellationToken ct = default);
    Task RecomputeCountsAsync(long rootId, CancellationToken ct = default);
}

public interface IGenreRepository
{
    Task<IReadOnlyList<Genre>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<GenreAlias>> GetAliasesAsync(CancellationToken ct = default);
    /// <summary>Raised-by-edits counter; tracks whose GenreMapVersion is lower need remapping.</summary>
    Task<int> GetTaxonomyVersionAsync(CancellationToken ct = default);
    Task<int> BumpTaxonomyVersionAsync(CancellationToken ct = default);

    Task<long> AddAsync(string name, string normalizedName, long? parentId, CancellationToken ct = default);
    Task UpdateNameAsync(long id, string name, string normalizedName, CancellationToken ct = default);
    Task SetParentAsync(long id, long? parentId, CancellationToken ct = default);
    /// <summary>Removes the genre, its aliases and every track assignment to it. Tracks and their raw text are untouched.</summary>
    Task DeleteAsync(long id, CancellationToken ct = default);
    /// <summary>False when the alias already exists.</summary>
    Task<bool> AddAliasAsync(string aliasNormalized, long genreId, CancellationToken ct = default);
    Task RemoveAliasAsync(string aliasNormalized, CancellationToken ct = default);
    /// <summary>
    /// Distinct present tracks in each genre or any of its descendants, so a track in both "Disco" and "Nu Disco"
    /// counts once for "Disco". Genres without tracks are absent.
    /// </summary>
    Task<IReadOnlyDictionary<long, int>> GetTrackCountsAsync(CancellationToken ct = default);
}

/// <summary>A track's raw genre text as read when it was selected for mapping, and the genres it maps to.</summary>
public sealed record TrackGenreMapping(long TrackId, string? RawGenre, IReadOnlyList<long> GenreIds);

public interface ITrackGenreRepository
{
    /// <summary>Tracks whose genre mapping is older than <paramref name="version"/>.</summary>
    Task<IReadOnlyList<(long TrackId, string? RawGenre)>> GetStaleAsync(int version, int limit, CancellationToken ct = default);
    /// <summary>
    /// Replaces the Embedded mapping of each track and stamps it with <paramref name="version"/>. A track whose
    /// raw genre changed since it was read is skipped (it stays stale). User assignments are never touched.
    /// </summary>
    Task<int> ReplaceEmbeddedAsync(IReadOnlyList<TrackGenreMapping> mappings, int version, CancellationToken ct = default);
    /// <summary>Adds or removes a user-provenance assignment for several tracks at once.</summary>
    Task SetUserGenreAsync(IReadOnlyList<long> trackIds, long genreId, bool assigned, CancellationToken ct = default);
    /// <summary>All assignments (any provenance) of the given tracks.</summary>
    Task<IReadOnlyList<(long TrackId, long GenreId, Domain.Enums.GenreSource Source)>> GetForTracksAsync(
        IReadOnlyList<long> trackIds, CancellationToken ct = default);
    /// <summary>Distinct raw genre strings of present tracks with how many tracks carry each.</summary>
    Task<IReadOnlyList<(string RawGenre, int Count)>> GetRawGenreCountsAsync(CancellationToken ct = default);
}

public interface IDjSessionRepository
{
    Task<long> StartAsync(DateTime startedUtc, string? djSoftware, string? name, bool autoStarted, CancellationToken ct = default);
    Task EndAsync(long id, DateTime endedUtc, CancellationToken ct = default);
    Task UpdateAsync(long id, string? djSoftware, string? name, CancellationToken ct = default);
    Task<DjSession?> GetAsync(long id, CancellationToken ct = default);
    /// <summary>The session that has not ended yet (the newest one if, after a crash, several never ended).</summary>
    Task<DjSession?> GetActiveAsync(CancellationToken ct = default);
    Task<IReadOnlyList<DjSession>> GetRecentAsync(int limit, CancellationToken ct = default);
    /// <summary>When something last happened in the session, or null when it has no events yet.</summary>
    Task<DateTime?> GetLastEventUtcAsync(long sessionId, CancellationToken ct = default);
}

public interface ITrackEventRepository
{
    /// <summary>Appends events in one transaction and returns their ids in the same order.</summary>
    Task<IReadOnlyList<long>> AddAsync(IReadOnlyList<TrackEvent> events, CancellationToken ct = default);
    Task<IReadOnlyList<TrackEvent>> GetForSessionAsync(long sessionId, CancellationToken ct = default);
    Task<IReadOnlyList<TrackEvent>> GetForTrackAsync(long trackId, int limit, CancellationToken ct = default);
    Task<TrackEvent?> GetAsync(long id, CancellationToken ct = default);
    /// <summary>The Exported events of a track in a session, newest first.</summary>
    Task<IReadOnlyList<TrackEvent>> GetAnchorsForTrackAsync(long sessionId, long trackId, CancellationToken ct = default);
}

public interface IPlayHistoryRepository
{
    /// <summary>
    /// Adds a play. Returns null (and changes nothing) when a play with the same (Source, ExternalEventKey) exists already,
    /// which is what makes repeated imports and repeated heuristic resolution idempotent.
    /// </summary>
    Task<long?> AddAsync(PlayRecord play, CancellationToken ct = default);
    /// <summary>
    /// Removes the inferred or manual plays that grew out of an Exported event and returns them, so the change can be undone.
    /// Plays that a DJ application confirmed (imported history) are evidence from outside and are never removed here.
    /// </summary>
    Task<IReadOnlyList<PlayRecord>> DeleteByAnchorAsync(long anchorEventId, CancellationToken ct = default);
    /// <summary>The same for a track's most recent inferred or manual play when there is no export to point at.</summary>
    Task<IReadOnlyList<PlayRecord>> DeleteLatestRemovableAsync(long trackId, CancellationToken ct = default);
    Task DeleteAsync(long playId, CancellationToken ct = default);
    /// <summary>Puts a previously deleted play back with its original id.</summary>
    Task RestoreAsync(PlayRecord play, CancellationToken ct = default);
    Task MarkSupersededAsync(long possiblePlayId, long confirmedPlayId, CancellationToken ct = default);
    /// <summary>The track's inferred or manual plays (not yet replaced by anything) between the two times, for an imported play to take over.</summary>
    Task<IReadOnlyList<PlayRecord>> FindUpgradableAsync(long trackId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
    Task<IReadOnlyList<Models.PlayHistoryItem>> GetForTrackAsync(long trackId, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<Models.SessionEntryRow>> GetSessionEntriesAsync(long sessionId, CancellationToken ct = default);
}

public interface ICustomTagRepository
{
    Task<IReadOnlyList<CustomTag>> GetAllAsync(CancellationToken ct = default);
    Task<long> AddAsync(string name, CancellationToken ct = default);
    Task RenameAsync(long id, string name, CancellationToken ct = default);
    /// <summary>Deletes the tag and its assignments; tracks are untouched.</summary>
    Task DeleteAsync(long id, CancellationToken ct = default);
    /// <summary>Present tracks carrying each tag.</summary>
    Task<IReadOnlyDictionary<long, int>> GetTrackCountsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<(long TrackId, long TagId)>> GetAssignmentsAsync(IReadOnlyList<long> trackIds, CancellationToken ct = default);
    Task AssignAsync(IReadOnlyList<long> trackIds, long tagId, CancellationToken ct = default);
    Task UnassignAsync(IReadOnlyList<long> trackIds, long tagId, CancellationToken ct = default);
}

public interface ITrackRepository
{
    Task UpsertBatchAsync(IReadOnlyList<Track> tracks, CancellationToken ct = default);
    Task<Track?> GetAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<Track>> GetManyAsync(IReadOnlyList<long> ids, CancellationToken ct = default);
    /// <summary>
    /// Stores what was read back from a file after its tags were written, together with the file's new size and
    /// modified time (so the next scan sees an up-to-date file and does not re-read it). Search indexes follow via triggers;
    /// genre mapping is marked stale so it is recomputed.
    /// </summary>
    Task ApplyMetadataAsync(long trackId, Models.TrackMetadata metadata, long fileSize, DateTime modifiedUtc, string normalizedText,
        CancellationToken ct = default);
    Task<IReadOnlyList<IndexedFile>> GetIndexedFilesAsync(long rootId, CancellationToken ct = default);
    /// <summary>Marks unchanged tracks as seen in the current scan (IndexedUtc = scanUtc, Missing = 0).</summary>
    Task TouchAsync(IReadOnlyList<long> ids, DateTime scanUtc, CancellationToken ct = default);
    /// <summary>Flags tracks under the root not seen since scanStartUtc as Missing. Never deletes.</summary>
    Task<int> MarkMissingAsync(long rootId, DateTime scanStartUtc, CancellationToken ct = default);
    Task<int> CountAsync(CancellationToken ct = default);
    /// <summary>Distinct raw genre strings of present tracks (callers split multi-value tags).</summary>
    Task<IReadOnlyList<string>> GetDistinctGenresAsync(CancellationToken ct = default);
}

/// <summary>Per-track text prepared for smart Greek search (migration 0005).</summary>
public interface ITrackSearchAliasRepository
{
    /// <summary>Tracks whose alias row is older than <paramref name="version"/> (or missing), with the text it is built from.</summary>
    Task<IReadOnlyList<SearchAliasStaleRow>> GetStaleAsync(int version, int limit, CancellationToken ct = default);

    Task<int> CountStaleAsync(int version, CancellationToken ct = default);

    /// <summary>
    /// Writes the alias rows and stamps the tracks with <paramref name="version"/>. A track whose text changed since it was read is
    /// skipped (it stays stale). Returns how many rows were applied.
    /// </summary>
    Task<int> UpsertAsync(IReadOnlyList<(SearchAliasStaleRow Source, TrackSearchAlias Alias)> rows, int version, CancellationToken ct = default);

    /// <summary>Makes these tracks stale so the next index run rebuilds them (used when the user's alias list changes).</summary>
    Task MarkStaleAsync(IReadOnlyList<long> trackIds, CancellationToken ct = default);

    /// <summary>Ids of tracks whose normalized text contains the normalized phrase as whole words.</summary>
    Task<IReadOnlyList<long>> FindContainingAsync(string normalizedPhrase, CancellationToken ct = default);

    /// <summary>Ids of tracks whose alias row carries this normalized user alias.</summary>
    Task<IReadOnlyList<long>> FindWithExtraAsync(string normalizedAlias, CancellationToken ct = default);
}

public interface ISearchAliasOverrideRepository
{
    Task<IReadOnlyList<SearchAliasOverride>> GetAllAsync(CancellationToken ct = default);
    /// <summary>Adds an override; returns null when the same pair already exists.</summary>
    Task<SearchAliasOverride?> AddAsync(string source, string alias, string sourceNormalized, string aliasNormalized, CancellationToken ct = default);
    /// <summary>Removes an override and returns it (null if it did not exist).</summary>
    Task<SearchAliasOverride?> RemoveAsync(long id, CancellationToken ct = default);
}

/// <summary>Reads the facts Find Similar compares, straight from the library.</summary>
public interface ISimilarCandidateSource
{
    Task<SimilarSignals?> GetSeedAsync(long trackId, CancellationToken ct = default);

    /// <summary>
    /// Tracks worth scoring against the seed: within a tempo window (also at half/double tempo when allowed), or sharing a related genre or a DJ tag.
    /// A deterministic cap keeps this bounded on a huge library; the nearest tempos are kept first.
    /// </summary>
    Task<IReadOnlyList<SimilarSignals>> GetCandidatesAsync(SimilarSignals seed, SimilarTracksOptions options,
        IReadOnlyCollection<long> relatedGenreIds, CancellationToken ct = default);
}
