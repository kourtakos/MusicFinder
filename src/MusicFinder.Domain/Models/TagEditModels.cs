namespace MusicFinder.Domain.Models;

/// <summary>One line of the preview: what a field is now and what it would become for one track.</summary>
public sealed record TagDiffRow(long TrackId, string FileName, TagField Field, string? Before, string? After);

/// <summary>What would happen if the edit were applied. Nothing has been written when you have this.</summary>
public sealed record TagEditPlan(
    IReadOnlyList<string> Errors,
    IReadOnlyList<TagDiffRow> Changes,
    int TracksWithChanges,
    int TracksUnchanged);

public enum TagEditOutcome { Written, Unchanged, Failed }

public sealed record TagEditFileResult(
    long TrackId, string Path, TagEditOutcome Outcome, string? Error = null, string? BackupPath = null, string? Warning = null);

public sealed record TagEditProgress(int Done, int Total, string CurrentFile);

public sealed record TagEditSummary(IReadOnlyList<TagEditFileResult> Files, bool Cancelled)
{
    public int Written => Files.Count(f => f.Outcome == TagEditOutcome.Written);
    public int Unchanged => Files.Count(f => f.Outcome == TagEditOutcome.Unchanged);
    public int Failed => Files.Count(f => f.Outcome == TagEditOutcome.Failed);
}
