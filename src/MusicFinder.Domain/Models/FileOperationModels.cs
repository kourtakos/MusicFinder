namespace MusicFinder.Domain.Models;

public static class OpState
{
    public const string Planned = "Planned";
    public const string InProgress = "InProgress";
    public const string Done = "Done";
    public const string Failed = "Failed";
    public const string Skipped = "Skipped";
    public const string UndoInProgress = "UndoInProgress";
    public const string Undone = "Undone";
}

public static class OpRole
{
    public const string Track = "Track";
    public const string Sidecar = "Sidecar";
}

public sealed record NewJournalOp(long TrackId, string Role, string OldPath, string NewPath);

public sealed record JournalOp(long Id, string BatchId, long TrackId, string Role, string OldPath, string NewPath, string State, string? Error);

public sealed record BatchInfo(string Id, string Kind, string? Template, DateTime CreatedUtc, int Done, int Failed, int Undone, int Skipped)
{
    public bool CanUndo => Done > 0;
}

public sealed record FileOperationProgress(int Total, int Done, int Failed, string? Current);

public sealed record FileFailure(string Path, string Reason);

public sealed record BatchResult(string? BatchId, int Renamed, int SidecarsRenamed, IReadOnlyList<FileFailure> Failures, bool Cancelled, string? Refused = null);

public sealed record UndoResult(int Restored, IReadOnlyList<FileFailure> Refused);

public sealed record RecoveryResult(int Completed, int RolledBack, IReadOnlyList<FileFailure> NeedAttention);

public sealed record PathMapEntry(string OldPath, string NewPath);
