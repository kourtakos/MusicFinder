using MusicFinder.Domain.Enums;

namespace MusicFinder.Domain.Models;

/// <summary>Values stored in the Source columns. Text rather than an enum so M4 importers add sources without a schema change.</summary>
public static class PlaySources
{
    public const string InactivityHeuristic = "InactivityHeuristic";
    public const string User = "User";
    public const string DragExport = "DragExport";
    public const string VirtualDj = "VirtualDJ";
    public const string Traktor = "Traktor";
}

/// <summary>What the drag source observed. <see cref="Effect"/> is the OS drop effect, which is NOT proof the target loaded anything.</summary>
public sealed record DragRecord(
    IReadOnlyList<long> TrackIds, DateTime StartedUtc, DateTime CompletedUtc, string Effect, string? ForegroundProcess = null);

/// <summary>One export of one track, with how it was resolved. Built by the repository from the event log and play history.</summary>
public sealed record SessionEntryRow(
    long AnchorEventId, long TrackId, string Title, string? Artist, string FileName, DateTime ExportedUtc, string? ExportEvidenceJson,
    long? PlayId, string? PlayStatus, string? PlaySource, DateTime? PlayedUtc,
    string? ResolutionEventType, string? ResolutionEvidenceJson);

/// <summary>An entry in the Session tab: what the user did and how sure we are about it.</summary>
public sealed record SessionEntry(
    long AnchorEventId, long TrackId, string Title, string? Artist, DateTime ExportedUtc, SessionPlayStatus Status,
    string Evidence, string Effect, string? Target);

/// <summary>One line of a track's play history (details panel).</summary>
public sealed record PlayHistoryItem(
    long Id, DateTime PlayedUtc, PlayStatus Status, string Source, long? SessionId, string? SessionName, bool Superseded)
{
    /// <summary>"Confirmed (VirtualDJ)", "Confirmed (manual)", "Possibly played".</summary>
    public string Label => (Status, Source) switch
    {
        (PlayStatus.Possible, _) => "Possibly played",
        (PlayStatus.Confirmed, PlaySources.User) => "Confirmed (manual)",
        (PlayStatus.Confirmed, var s) => $"Confirmed ({s})",
        _ => "Played"
    };
}
