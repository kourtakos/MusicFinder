using MusicFinder.Domain.Models;

namespace MusicFinder.Domain.Contracts;

/// <summary>The only code that moves a file. Tests replace it; the real one never overwrites.</summary>
public interface IFileMover
{
    /// <summary>True when the path exists (case-insensitive, as Windows sees it).</summary>
    bool Exists(string path);
    /// <summary>True only when the folder entry has exactly this spelling (tells apart a case-only rename that did or did not happen).</summary>
    bool ExistsExact(string path);
    /// <summary>Renames within a volume. Throws if the destination already exists; never overwrites.</summary>
    void Move(string from, string to);
}

/// <summary>Keeps a scan and a batch of file operations from running together.</summary>
public interface ILibraryWriteLock
{
    /// <summary>Null when something else (a scan, another batch) holds it right now.</summary>
    IDisposable? TryEnter();
    Task<IDisposable> EnterAsync(CancellationToken ct);
}

public interface IFileOperationJournal
{
    /// <summary>Writes the batch and every operation as Planned. Returns the batch id and the operations with their ids.</summary>
    Task<(string BatchId, IReadOnlyList<JournalOp> Ops)> BeginBatchAsync(string kind, string? template, IReadOnlyList<NewJournalOp> ops, CancellationToken ct);
    /// <summary>
    /// Sets the state of one operation. When <paramref name="trackPath"/> is given, the track's row (path, file name, search text)
    /// is updated in the same transaction, so the database never disagrees with the journal.
    /// </summary>
    Task SetStateAsync(long opId, string state, string? error, string? trackPath, CancellationToken ct);
    Task<IReadOnlyList<JournalOp>> GetOpsAsync(string batchId, CancellationToken ct);
    Task<IReadOnlyList<JournalOp>> GetUnfinishedAsync(CancellationToken ct);
    Task<IReadOnlyList<BatchInfo>> GetBatchesAsync(int take, CancellationToken ct);
    /// <summary>The path the library currently has for a track (null when the track is unknown).</summary>
    Task<string?> GetTrackPathAsync(long trackId, CancellationToken ct);
}

public interface IRenameSourceReader
{
    Task<IReadOnlyList<RenameSource>> GetAsync(IReadOnlyCollection<long> trackIds, CancellationToken ct);
}

public interface IFileOperationService
{
    /// <summary>Renames the files of a confirmed plan. Journals first, never overwrites, one failure does not stop the batch.</summary>
    Task<BatchResult> RenameAsync(IReadOnlyList<PlanItem> plan, string template, IProgress<FileOperationProgress>? progress, CancellationToken ct);
    /// <summary>Restores the old names of a batch, newest first. Anything that no longer fits is refused with a reason, not forced.</summary>
    Task<UndoResult> UndoAsync(string batchId, IProgress<FileOperationProgress>? progress, CancellationToken ct);
    /// <summary>Settles operations a crash left half-done. Run at startup before the first scan.</summary>
    Task<RecoveryResult> RecoverAsync(CancellationToken ct);
    Task<IReadOnlyList<BatchInfo>> GetBatchesAsync(int take, CancellationToken ct);
    /// <summary>Old and new paths of every file the batch renamed and has not undone.</summary>
    Task<IReadOnlyList<PathMapEntry>> GetPathMapAsync(string batchId, CancellationToken ct);
}
