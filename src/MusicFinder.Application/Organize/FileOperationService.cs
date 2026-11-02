using Microsoft.Extensions.Logging;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.Application.Organize;

/// <summary>
/// Runs a confirmed rename plan. The order for every file is fixed: re-check, write the journal row as InProgress, move,
/// then mark Done together with the database row. Nothing is ever overwritten, deleted or forced.
/// </summary>
public sealed class FileOperationService : IFileOperationService
{
    private readonly IFileMover _files;
    private readonly IFileOperationJournal _journal;
    private readonly ILibraryWriteLock _lock;
    private readonly ILogger<FileOperationService> _log;

    public FileOperationService(IFileMover files, IFileOperationJournal journal, ILibraryWriteLock writeLock, ILogger<FileOperationService> log)
    {
        _files = files;
        _journal = journal;
        _lock = writeLock;
        _log = log;
    }

    public async Task<BatchResult> RenameAsync(IReadOnlyList<PlanItem> plan, string template, IProgress<FileOperationProgress>? progress, CancellationToken ct, string kind = "Rename")
    {
        var todo = plan.Where(p => p.WillMove).ToList();
        if (todo.Count == 0) return new BatchResult(null, 0, 0, [], false);

        using var gate = _lock.TryEnter();
        if (gate is null)
            return new BatchResult(null, 0, 0, [], false, "A library scan or another file operation is running. Try again when it has finished.");

        var newOps = new List<NewJournalOp>();
        foreach (var item in todo)
        {
            newOps.Add(new NewJournalOp(item.TrackId, OpRole.Track, item.OldPath, item.NewPath));
            foreach (var sc in item.Sidecars) newOps.Add(new NewJournalOp(item.TrackId, OpRole.Sidecar, sc.OldPath, sc.NewPath));
        }
        var (batchId, ops) = await _journal.BeginBatchAsync(kind, template, newOps, CancellationToken.None);

        var failures = new List<FileFailure>();
        int renamed = 0, sidecars = 0, handled = 0;
        var cancelled = false;
        var index = 0;

        foreach (var item in todo)
        {
            var group = new List<JournalOp>();
            while (index < ops.Count && ops[index].TrackId == item.TrackId && (group.Count == 0 || ops[index].Role == OpRole.Sidecar))
                group.Add(ops[index++]);

            if (ct.IsCancellationRequested)
            {
                cancelled = true;
                foreach (var op in group) await _journal.SetStateAsync(op.Id, OpState.Skipped, "Cancelled before this file.", null, CancellationToken.None);
                continue;
            }

            progress?.Report(new FileOperationProgress(todo.Count, handled, failures.Count, item.OldPath));
            var trackOk = true;
            foreach (var op in group)
            {
                if (op.Role == OpRole.Sidecar && !trackOk)
                {
                    await _journal.SetStateAsync(op.Id, OpState.Skipped, "The track itself was not renamed.", null, CancellationToken.None);
                    continue;
                }
                var error = await MoveOneAsync(op, forward: true);
                if (error is null)
                {
                    if (op.Role == OpRole.Track) renamed++; else sidecars++;
                }
                else
                {
                    failures.Add(new FileFailure(op.OldPath, error));
                    if (op.Role == OpRole.Track) trackOk = false;
                }
            }
            handled++;
        }
        await _journal.RecomputeFolderCountsAsync(batchId, CancellationToken.None);
        progress?.Report(new FileOperationProgress(todo.Count, handled, failures.Count, null));
        _log.LogInformation("Rename batch {Batch}: {Renamed} renamed, {Failed} failed, cancelled={Cancelled}", batchId, renamed, failures.Count, cancelled);
        return new BatchResult(batchId, renamed, sidecars, failures, cancelled);
    }

    /// <summary>The single place a file is renamed. Returns null on success, otherwise the reason (the journal row records it).</summary>
    private async Task<string?> MoveOneAsync(JournalOp op, bool forward)
    {
        var from = forward ? op.OldPath : op.NewPath;
        var to = forward ? op.NewPath : op.OldPath;
        var caseOnly = string.Equals(from, to, StringComparison.OrdinalIgnoreCase);
        var settled = forward ? OpState.Done : OpState.Undone;
        var before = forward ? OpState.Failed : OpState.Done;
        string? problem = null;
        if (!forward && op.Role == OpRole.Track)
        {
            var known = await _journal.GetTrackPathAsync(op.TrackId, CancellationToken.None);
            if (!string.Equals(known, op.NewPath, StringComparison.OrdinalIgnoreCase))
                problem = "The library has this file under another name now (a later rename). Undo that one first.";
        }
        if (problem is null && !_files.ExistsExact(from)) problem = forward ? "The file is no longer where the library expects it." : "The renamed file is no longer there.";
        if (problem is null && !caseOnly && _files.Exists(to)) problem = forward ? "A file with the new name appeared since the preview." : "A file already has the old name.";
        if (problem is not null)
        {
            if (forward) await _journal.SetStateAsync(op.Id, OpState.Failed, problem, null, CancellationToken.None);
            return problem;
        }

        await _journal.SetStateAsync(op.Id, forward ? OpState.InProgress : OpState.UndoInProgress, null, null, CancellationToken.None);
        try
        {
            if (!string.Equals(Path.GetDirectoryName(from), Path.GetDirectoryName(to), StringComparison.OrdinalIgnoreCase))
                _files.EnsureFolder(Path.GetDirectoryName(to)!);
            _files.Move(from, to);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The move did not happen (a failed rename leaves the file where it was): put the journal back and say why.
            var why = ex is UnauthorizedAccessException ? "Access denied (read-only or locked file)." : ex.Message;
            await _journal.SetStateAsync(op.Id, before, why, null, CancellationToken.None);
            return why;
        }
        await _journal.SetStateAsync(op.Id, settled, null, op.Role == OpRole.Track ? to : null, CancellationToken.None);
        return null;
    }

    public async Task<UndoResult> UndoAsync(string batchId, IProgress<FileOperationProgress>? progress, CancellationToken ct)
    {
        using var gate = _lock.TryEnter();
        if (gate is null) return new UndoResult(0, [new FileFailure("", "A library scan or another file operation is running. Try again when it has finished.")]);

        var ops = (await _journal.GetOpsAsync(batchId, ct)).Where(o => o.State == OpState.Done).OrderByDescending(o => o.Id).ToList();
        var refused = new List<FileFailure>();
        var restored = 0;
        var done = 0;
        foreach (var op in ops)
        {
            if (ct.IsCancellationRequested) break;
            progress?.Report(new FileOperationProgress(ops.Count, done++, refused.Count, op.NewPath));
            var error = await MoveOneAsync(op, forward: false);
            if (error is null) restored++; else refused.Add(new FileFailure(op.NewPath, error));
        }
        await _journal.RecomputeFolderCountsAsync(batchId, CancellationToken.None);
        return new UndoResult(restored, refused);
    }

    public async Task<RecoveryResult> RecoverAsync(CancellationToken ct)
    {
        var completed = 0;
        var rolledBack = 0;
        var attention = new List<FileFailure>();
        foreach (var op in await _journal.GetUnfinishedAsync(ct))
        {
            var undo = op.State == OpState.UndoInProgress;
            var source = undo ? op.NewPath : op.OldPath;        // where the file was before the interrupted move
            var target = undo ? op.OldPath : op.NewPath;        // where it would be if the move had finished
            var atSource = _files.ExistsExact(source);
            var atTarget = _files.ExistsExact(target);
            var movedState = undo ? OpState.Undone : OpState.Done;
            var unmovedState = undo ? OpState.Done : OpState.Failed;

            if (atTarget && !atSource)
            {
                await _journal.SetStateAsync(op.Id, movedState, null, op.Role == OpRole.Track ? target : null, CancellationToken.None);
                completed++;
            }
            else if (atSource && !atTarget)
            {
                await _journal.SetStateAsync(op.Id, unmovedState, "Interrupted before the file was moved.", null, CancellationToken.None);
                rolledBack++;
            }
            else
            {
                var why = atSource ? "Both the old and the new file exist; check them in Explorer." : "Neither the old nor the new file was found.";
                await _journal.SetStateAsync(op.Id, OpState.Failed, why, null, CancellationToken.None);
                attention.Add(new FileFailure(source, why));
            }
        }
        if (completed + rolledBack + attention.Count > 0)
            _log.LogWarning("Journal recovery: {Completed} finished, {RolledBack} not moved, {Attention} need attention", completed, rolledBack, attention.Count);
        return new RecoveryResult(completed, rolledBack, attention);
    }

    public Task<IReadOnlyList<BatchInfo>> GetBatchesAsync(int take, CancellationToken ct) => _journal.GetBatchesAsync(take, ct);

    public async Task<IReadOnlyList<PathMapEntry>> GetPathMapAsync(string batchId, CancellationToken ct) =>
        (await _journal.GetOpsAsync(batchId, ct)).Where(o => o.State == OpState.Done)
            .Select(o => new PathMapEntry(o.OldPath, o.NewPath)).ToList();
}
