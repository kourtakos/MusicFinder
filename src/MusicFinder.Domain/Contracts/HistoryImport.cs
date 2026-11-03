using MusicFinder.Domain.Models;

namespace MusicFinder.Domain.Contracts;

/// <summary>Finds library tracks for a play reported by a DJ program. Each method returns every track that fits, so the caller can tell "one" from "ambiguous".</summary>
public interface IHistoryTrackLookup
{
    /// <summary>The full path, compared without regard to case.</summary>
    Task<IReadOnlyList<long>> FindByPathAsync(string path, CancellationToken ct = default);
    Task<IReadOnlyList<long>> FindByNameAndSizeAsync(string fileName, long size, CancellationToken ct = default);
    Task<IReadOnlyList<long>> FindByNameAndDurationAsync(string fileName, double seconds, double toleranceSeconds, CancellationToken ct = default);
}

/// <summary>What an import remembers between runs: the history files it has read, and the plays it could not place.</summary>
public interface IHistoryImportStore
{
    Task<IReadOnlyDictionary<string, HistoryFileState>> GetFileStatesAsync(string source, CancellationToken ct = default);
    Task SaveFileStateAsync(string source, HistoryFileState state, CancellationToken ct = default);
    /// <summary>Stores a play that has no single matching track. Returns false when it is already stored (re-imports do not pile up).</summary>
    Task<bool> AddUnmatchedAsync(UnmatchedHistoryEvent entry, CancellationToken ct = default);
    Task<IReadOnlyList<UnmatchedHistoryEvent>> GetUnmatchedAsync(string status, CancellationToken ct = default);
    Task<int> CountPendingAsync(CancellationToken ct = default);
    Task SetUnmatchedStatusAsync(long id, string status, long? trackId, CancellationToken ct = default);
}

/// <summary>Records one play that a DJ program reported for a library track, upgrading the drag-based guess when there is one.</summary>
public interface IImportedPlayRecorder
{
    Task<ImportedPlayOutcome> RecordImportedPlayAsync(
        ImportedPlayEvent play, long trackId, string matchedBy, double confidence, CancellationToken ct = default);
}
