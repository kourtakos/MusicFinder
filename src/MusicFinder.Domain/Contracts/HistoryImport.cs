using MusicFinder.Domain.Models;

namespace MusicFinder.Domain.Contracts;

/// <summary>How soon a DJ program's history shows up on disk.</summary>
public enum HistorySyncMode
{
    /// <summary>Written while playing (VirtualDJ appends after each track).</summary>
    Live,
    /// <summary>Written when the program is closed or exports (Traktor archives its history on close).</summary>
    AfterClose
}

/// <summary>What one pass over a DJ program's history folder produced.</summary>
public sealed record HistoryReadResult(
    IReadOnlyList<ImportedPlayEvent> Events, IReadOnlyList<HistoryFileState> ReadFiles, IReadOnlyList<string> Warnings, int FilesRead, string? Error = null);

/// <summary>Reads one DJ program's history files. Read-only: it never writes to the DJ program's folders and never changes its settings.</summary>
public interface IHistoryImporter
{
    string ProviderId { get; }
    string DisplayName { get; }
    HistorySyncMode SyncMode { get; }
    /// <summary>History folders that exist on this PC, best guess first.</summary>
    IReadOnlyList<string> DetectDefaultFolders();
    /// <summary>
    /// Reads the files in <paramref name="folder"/> that are new or changed since <paramref name="known"/>. A file still being written is left out of
    /// <see cref="HistoryReadResult.ReadFiles"/>, so the next pass reads it again.
    /// </summary>
    Task<HistoryReadResult> ReadAsync(string folder, IReadOnlyDictionary<string, HistoryFileState> known, CancellationToken ct);
}

/// <summary>The totals of one import run for one DJ program, as shown to the user.</summary>
public sealed record HistoryImportResult(
    string ProviderId, string DisplayName, string? Folder, int Files, int Entries, int New, int Upgraded, int Duplicates, int Unmatched, int Ambiguous,
    IReadOnlyList<string> Warnings, string? Error)
{
    public string Summary =>
        Error is not null ? Error
        : Entries == 0 ? "Nothing new."
        : $"{New + Upgraded} new ({Upgraded} confirmed a guess), {Duplicates} already known, {Unmatched + Ambiguous} not matched.";
}

/// <summary>Reads the DJ programs' history and records the plays. One run at a time.</summary>
public interface IHistoryImportService
{
    IReadOnlyList<IHistoryImporter> Importers { get; }
    /// <summary>The folder an import would read now: the one the user chose, else the first one found. Null when there is none.</summary>
    string? ResolveFolder(string providerId);
    /// <summary>Imports one program, or all of them when <paramref name="providerId"/> is null.</summary>
    Task<IReadOnlyList<HistoryImportResult>> RunAsync(string? providerId, CancellationToken ct = default);
    /// <summary>Ties a pending unmatched play to a track chosen by the user.</summary>
    Task<ImportedPlayOutcome> LinkAsync(long unmatchedId, long trackId, CancellationToken ct = default);
    Task IgnoreAsync(long unmatchedId, CancellationToken ct = default);
    event EventHandler<IReadOnlyList<HistoryImportResult>>? ImportCompleted;
}

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
