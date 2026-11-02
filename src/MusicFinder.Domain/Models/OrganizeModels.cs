namespace MusicFinder.Domain.Models;

/// <summary>What a rename template can use from one indexed track. Nothing here is read from disk at planning time.</summary>
public sealed record RenameSource(
    long TrackId, string FullPath, string? Artist, string? Title, string? Album, string? AlbumArtist,
    int? Year, int? TrackNumber, string? Genre, double? Bpm, string? Key, string? RootPath = null);

public enum PlanStatus
{
    /// <summary>The file will get a new name.</summary>
    Rename,
    /// <summary>Only upper/lower case of the name changes.</summary>
    CaseOnly,
    /// <summary>The name would not change; nothing to do.</summary>
    Unchanged,
    /// <summary>Not touched, with a visible reason (missing tag, collision, too long, file not found...).</summary>
    Skipped,
}

/// <summary>A small file that travels with a track (lyrics, cue sheet).</summary>
public sealed record PlannedSidecar(string OldPath, string NewPath);

public sealed record PlanItem(
    long TrackId, string OldPath, string NewPath, PlanStatus Status, string? Reason, IReadOnlyList<PlannedSidecar> Sidecars)
{
    public bool WillMove => Status is PlanStatus.Rename or PlanStatus.CaseOnly;
}

/// <summary>With <paramref name="MoveIntoFolders"/> the template may contain "/" and describes the path below the track's library folder; otherwise only the file name changes.</summary>
public sealed record RenameOptions(string Template, bool UsePlaceholders = false, bool IncludeSidecars = true, bool MoveIntoFolders = false);
