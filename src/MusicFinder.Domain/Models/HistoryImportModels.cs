namespace MusicFinder.Domain.Models;

/// <summary>
/// One play reported by a DJ program's own history (VirtualDJ, Traktor). It is evidence about the DJ program, not about a track in the library:
/// matching it to a library track is a separate step. <see cref="ExternalEventKey"/> is stable across re-reads of the same file, so importing twice
/// never counts a play twice.
/// </summary>
public sealed record ImportedPlayEvent(
    string Source, string ExternalEventKey, DateTime PlayedUtc, string FilePath,
    string? Artist, string? Title, string? Remix, long? FileSize, double? DurationSeconds);

/// <summary>What happened to one imported play.</summary>
public enum ImportedPlayOutcome
{
    /// <summary>A new Confirmed play with no earlier guess behind it.</summary>
    Added,
    /// <summary>A Possible (or manual) play from a drag was replaced by the confirmed one.</summary>
    Upgraded,
    /// <summary>This play was imported before; nothing changed.</summary>
    Duplicate
}

/// <summary>A history file that was read completely: its size and time when read, so an unchanged file is skipped next time.</summary>
public sealed record HistoryFileState(string FilePath, long Length, DateTime LastWriteUtc);

public static class UnmatchedStatus
{
    public const string Pending = "Pending";
    public const string Linked = "Linked";
    public const string Ignored = "Ignored";
}

/// <summary>A reported play that could not be tied to exactly one library track.</summary>
public sealed record UnmatchedHistoryEvent(
    long Id, string Source, string ExternalEventKey, DateTime PlayedUtc, string SourcePath, string? Artist, string? Title,
    long? FileSize, double? DurationSeconds, string Reason, string? CandidatesJson, string Status, long? ResolvedTrackId);
